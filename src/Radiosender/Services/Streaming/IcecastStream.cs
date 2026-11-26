using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Streaming;

public sealed class IcecastStream : IDisposable
{
    private const int Bitrate = 128_000;
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

    /// <summary>Raised on the stream's own thread.</summary>
    public event EventHandler<string>? Failed;

    /// <exception cref="LiveStreamException">Server unreachable or login refused.</exception>
    public static async Task<IcecastStream> ConnectAsync(IcecastTarget target, IWaveProvider feed, CancellationToken cancellationToken = default)
    {
        // Security: values go into request line/headers; a line break would inject headers.
        if (new[] { target.Host, target.Path, target.User }.Any(value => value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            || target.User.Contains(':'))
            throw new LiveStreamException(Loc.T("Stream_InvalidChars"));

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
                    401 => Loc.T("Stream_LoginRefused"),
                    403 => Loc.T("Stream_MountBusy"),
                    404 => Loc.T("Stream_MountMissing"),
                    _ => Loc.F("Stream_Rejected", status),
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
                OperationCanceledException => new LiveStreamException(Loc.T("Stream_NoResponse"), ex),
                SocketException or IOException => new LiveStreamException(Loc.F("Stream_NoConnection", ex.Message), ex),
                _ => ex,
            };
        }
    }

    /// <exception cref="LiveStreamException">The server did not take the title.</exception>
    public static async Task UpdateTitleAsync(HttpClient http, IcecastTarget target, string title, CancellationToken cancellationToken = default)
    {
        // Plain http like the source connection: Icecast takes sources without TLS.
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
                throw new LiveStreamException(Loc.F("Stream_TitleRejected", (int)response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LiveStreamException(Loc.F("Stream_TitleFailed", ex.Message), ex);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _reader.Stop();
        // Encoder ends when reader returns 0; stalled connection is cut by closing the socket.
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
                ?? throw new LiveStreamException(Loc.T("Stream_NoEncoder"));
            using var encoder = new MediaFoundationEncoder(mediaType);
            encoder.Encode(_client.GetStream(), pcm, TranscodeContainerTypes.MFTranscodeContainerType_MP3);
            if (!_stopping)
                Failed?.Invoke(this, Loc.T("Stream_EndedUnexpectedly"));
        }
        catch (Exception ex)
        {
            // Stopping: socket closed under the encoder is expected.
            if (!_stopping)
                Failed?.Invoke(this, ex is LiveStreamException ? ex.Message : Loc.F("Stream_ConnectionLost", ex.Message));
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
                throw new LiveStreamException(Loc.T("Stream_NoValidReply"));
            header.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        var parts = header.ToString().Split(' ', 3);
        return parts.Length >= 2 && int.TryParse(parts[1], out var status)
            ? status
            : throw new LiveStreamException(Loc.T("Stream_InvalidReply"));
    }

    // Empty feed: wait instead of ending, so the encoder runs in real time.
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
            return 0; // encoder finishes
        }
    }
}
