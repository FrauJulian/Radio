using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Radiosender.Services;

/// <summary>Error of a live stream, with a message that can be shown to the user as is.</summary>
public class LiveStreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where and how a live source logs in. laut.fm shows these values in Radioadmin under "Live senden".</summary>
public record IcecastTarget(string Host, int Port, string Mount, string User, string Password)
{
    public string Path => Mount.StartsWith('/') ? Mount : "/" + Mount;

    public static IcecastTarget From(AppSettings settings) => new(
        settings.LautFmHost.Trim(), settings.LautFmPort, settings.LautFmMount.Trim(), settings.LautFmUser.Trim(), settings.LautFmPassword);

    /// <summary>Value of the Basic authorization header.</summary>
    public string Credentials => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    // The password stays out of logs and debugger views.
    public override string ToString() => $"{User}@{Host}:{Port}{Path}";
}

/// <summary>
/// A source connection to an Icecast server, which is what laut.fm takes for live broadcasts.
/// Reads 32-bit float audio from a feed and sends it as MP3 at 128 kbit/s until disposed.
/// </summary>
public sealed class IcecastStream : IDisposable
{
    public const int Bitrate = 128_000;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly FeedReader _reader;
    private readonly Thread _thread;
    private volatile bool _stopping;

    private IcecastStream(TcpClient client, IWaveProvider feed)
    {
        _client = client;
        _reader = new FeedReader(feed);
        _thread = new Thread(Run) { IsBackground = true, Name = "Icecast stream" };
    }

    /// <summary>The connection broke or the encoder stopped. Raised on the stream's own thread.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>Logs in at the server and starts sending.</summary>
    /// <param name="target">Server and login.</param>
    /// <param name="feed">32-bit float audio that returns 0 bytes while nothing new is there, like <see cref="BufferedWaveProvider"/>.</param>
    /// <param name="cancellationToken">Cancels the login.</param>
    /// <exception cref="LiveStreamException">The server cannot be reached or refused the login.</exception>
    public static async Task<IcecastStream> ConnectAsync(IcecastTarget target, IWaveProvider feed, CancellationToken cancellationToken = default)
    {
        // These values end up in the request line and headers; a line break there would change the request.
        if (new[] { target.Host, target.Path, target.User }.Any(value => value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            || target.User.Contains(':'))
            throw new LiveStreamException("Server, Mountpoint oder Benutzer enthalten ungültige Zeichen.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeout.Token);
            var network = client.GetStream();
            var request =
                $"PUT {target.Path} HTTP/1.1\r\n" +
                $"Host: {target.Host}:{target.Port}\r\n" +
                $"Authorization: Basic {target.Credentials}\r\n" +
                "User-Agent: Radiosender/1.0\r\n" +
                "Content-Type: audio/mpeg\r\n" +
                "Ice-Public: 0\r\n" +
                $"Ice-Bitrate: {Bitrate / 1000}\r\n" +
                "Expect: 100-continue\r\n\r\n";
            await network.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

            var status = await ReadStatusAsync(network, timeout.Token);
            if (status is not (100 or 200))
            {
                throw new LiveStreamException(status switch
                {
                    401 => "laut.fm hat die Anmeldung abgelehnt: Benutzer oder Passwort stimmen nicht.",
                    403 => "laut.fm nimmt gerade keinen Livestream an: Der Mountpoint ist belegt oder nicht freigeschaltet.",
                    404 => "Diesen Mountpoint gibt es auf dem Server nicht.",
                    _ => $"Der Server hat den Livestream abgelehnt (HTTP {status}).",
                });
            }

            var stream = new IcecastStream(client, feed);
            stream._thread.Start();
            return stream;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (ex is LiveStreamException || cancellationToken.IsCancellationRequested)
                throw;
            throw ex switch
            {
                OperationCanceledException => new LiveStreamException("Der laut.fm-Server antwortet nicht.", ex),
                SocketException or IOException => new LiveStreamException($"Keine Verbindung zum laut.fm-Server: {ex.Message}", ex),
                _ => ex,
            };
        }
    }

    /// <summary>Sets the title listeners see, through the server's admin interface with the same login.</summary>
    /// <exception cref="LiveStreamException">The server did not take the title.</exception>
    public static async Task UpdateTitleAsync(HttpClient http, IcecastTarget target, string title, CancellationToken cancellationToken = default)
    {
        // Plain http, like the source connection itself: Icecast takes sources without TLS.
        var url = new UriBuilder(Uri.UriSchemeHttp, target.Host, target.Port, "/admin/metadata")
        {
            Query = $"mount={Uri.EscapeDataString(target.Path)}&mode=updinfo&song={Uri.EscapeDataString(title)}",
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Basic", target.Credentials);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LiveStreamException($"laut.fm hat den Titel nicht angenommen (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LiveStreamException($"Der Titel konnte nicht an laut.fm gesendet werden: {ex.Message}", ex);
        }
    }

    /// <summary>Stops sending and closes the connection.</summary>
    public void Dispose()
    {
        _stopping = true;
        _reader.Stop();
        // The encoder ends once the reader returns 0; a stalled connection is cut by closing the socket.
        if (Thread.CurrentThread != _thread && _thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
        _client.Dispose();
    }

    private void Run()
    {
        try
        {
            MediaFoundationApi.Startup();
            var pcm = _reader.ToSampleProvider().ToWaveProvider16();
            var mediaType = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, pcm.WaveFormat, Bitrate)
                ?? throw new LiveStreamException("Windows hat keinen MP3-Encoder.");
            using var encoder = new MediaFoundationEncoder(mediaType);
            encoder.Encode(_client.GetStream(), pcm, TranscodeContainerTypes.MFTranscodeContainerType_MP3);
            if (!_stopping)
                Failed?.Invoke(this, "Der Livestream wurde unerwartet beendet.");
        }
        catch (Exception ex)
        {
            // While stopping, closing the socket under the encoder is expected.
            if (!_stopping)
                Failed?.Invoke(this, ex is LiveStreamException ? ex.Message : $"Verbindung zu laut.fm verloren: {ex.Message}");
        }
    }

    private static async Task<int> ReadStatusAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        var header = new StringBuilder();
        var buffer = new byte[512];
        while (!header.ToString().Contains("\r\n\r\n"))
        {
            var read = await network.ReadAsync(buffer, cancellationToken);
            if (read == 0 || header.Length > 8192)
                throw new LiveStreamException("Der Server hat die Verbindung ohne gültige Antwort beendet.");
            header.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        // "HTTP/1.1 100 Continue" or "HTTP/1.0 200 OK"
        var parts = header.ToString().Split(' ', 3);
        return parts.Length >= 2 && int.TryParse(parts[1], out var status)
            ? status
            : throw new LiveStreamException("Der Server hat ungültig geantwortet.");
    }

    /// <summary>Waits for audio instead of ending when the feed is empty, so the encoder keeps going in real time.</summary>
    private sealed class FeedReader(IWaveProvider feed) : IWaveProvider
    {
        private volatile bool _stopped;

        public WaveFormat WaveFormat => feed.WaveFormat;

        public void Stop() => _stopped = true;

        public int Read(Span<byte> buffer)
        {
            while (!_stopped)
            {
                var read = feed.Read(buffer);
                if (read > 0)
                    return read;
                Thread.Sleep(10);
            }
            return 0; // the encoder finishes and returns
        }
    }
}
