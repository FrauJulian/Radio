using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Settings;
using Radiosender.Services.Streaming;

namespace Radiosender.Tests;

public sealed class LautFmTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));

    private static readonly AppSettings Live = new()
    {
        LautFmEnabled = true,
        LautFmHost = "live.example.test",
        LautFmPort = 8080,
        LautFmMount = "/station",
        LautFmUser = "source",
        LautFmPassword = "geheim-passwort",
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Settings_CheckTheLiveDataOnlyWhenSwitchedOn()
    {
        Assert.Null(Live.Validate());
        Assert.Null(new AppSettings { LautFmEnabled = false, LautFmHost = "" }.Validate());
        Assert.Contains("Server", (Live with { LautFmHost = "" }).Validate());
        Assert.Contains("Port", (Live with { LautFmPort = 0 }).Validate());
        Assert.Contains("Mountpoint", (Live with { LautFmMount = " " }).Validate());
    }

    [Fact]
    public void Settings_StoreSecretsEncrypted()
    {
        var file = Path.Combine(_directory, "settings.json");

        Live.Save(file);
        var text = System.IO.File.ReadAllText(file);
        var loaded = AppSettings.Load(file);

        Assert.DoesNotContain(Live.LautFmPassword, text);
        Assert.Contains("\"LautFmPassword\"", text);
        Assert.Equal(Live, loaded);
        Assert.Equal("geheim-passwort", loaded.LautFmPassword);
        Assert.DoesNotContain("geheim", Live.ToString());
    }

    [Fact]
    public void Settings_SecretsThatCannotBeDecryptedAreEmpty()
    {
        var file = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(file, """{"LautFmPassword":"bm90IGVuY3J5cHRlZA=="}""");

        var loaded = AppSettings.Load(file);

        Assert.Equal("", loaded.LautFmPassword);
    }

    [Fact]
    public void StreamTitles_SendTheAdTriggerAtMostEvery15Minutes()
    {
        var now = new DateTime(2026, 11, 25, 12, 0, 0);
        var titles = new StreamTitles(() => now);
        var ad = new ScheduleItem(ScheduleItemKind.Ad, "Werbung", now, TimeSpan.FromMinutes(2));

        Assert.Equal(StreamTitles.AdBreak, titles.For(ad));
        now = now.AddMinutes(2);
        Assert.Equal("Werbung", titles.For(ad)); // second ad of the same break
        now = now.AddMinutes(12);
        Assert.Equal("Werbung", titles.For(ad)); // 14 minutes after the trigger
        now = now.AddMinutes(1);
        Assert.Equal(StreamTitles.AdBreak, titles.For(ad));

        var song = new Song { Title = "Lied", Artist = "Band", Duration = TimeSpan.FromMinutes(3), FilePath = "x.mp3" };
        Assert.Equal("Band - Lied", titles.For(new ScheduleItem(ScheduleItemKind.Song, "Lied", now, song.Duration, song)));
        Assert.Equal("Nachrichten", titles.For(new ScheduleItem(ScheduleItemKind.News, "Nachrichten", now, TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public async Task Icecast_LogsInWithPutAndSendsMp3()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var network = client.GetStream();
            var header = await ReadHeaderAsync(network);
            await network.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());
            var audio = new MemoryStream();
            var buffer = new byte[4096];
            while (audio.Length < 4000)
            {
                var read = await network.ReadAsync(buffer);
                if (read == 0)
                    break;
                audio.Write(buffer, 0, read);
            }
            return (header, audio.ToArray());
        });

        using var stream = await IcecastStream.ConnectAsync(new IcecastTarget("127.0.0.1", port, "live", "source", "pw"), Feed());
        var (header, audio) = await server.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.StartsWith("PUT /live HTTP/1.1\r\n", header);
        Assert.Contains($"Authorization: Basic {Convert.ToBase64String("source:pw"u8.ToArray())}\r\n", header);
        Assert.Contains("Content-Type: audio/mpeg\r\n", header);
        Assert.True(audio.Length >= 4000, $"only {audio.Length} bytes of audio arrived");
    }

    [Fact]
    public async Task Icecast_RefusedLoginIsAReadableError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var network = client.GetStream();
            await ReadHeaderAsync(network);
            await network.WriteAsync("HTTP/1.1 401 Authentication Required\r\n\r\n"u8.ToArray());
        });

        var error = await Assert.ThrowsAsync<LiveStreamException>(() =>
            IcecastStream.ConnectAsync(new IcecastTarget("127.0.0.1", port, "/live", "source", "falsch"), Feed()));
        await server;

        Assert.Contains("Passwort", error.Message);
    }

    [Fact]
    public async Task Icecast_RejectsLineBreaksInTheRequest()
    {
        await Assert.ThrowsAsync<LiveStreamException>(() =>
            IcecastStream.ConnectAsync(new IcecastTarget("127.0.0.1", 1, "live\r\nX: y", "source", "pw"), Feed()));
    }

    [Fact]
    public async Task Icecast_TitleGoesToTheAdminInterface()
    {
        HttpRequestMessage? sent = null;
        var http = new HttpClient(new FakeHandler(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await IcecastStream.UpdateTitleAsync(http, new IcecastTarget("live.example.test", 8080, "station", "source", "pw"), StreamTitles.AdBreak);

        Assert.Equal("http://live.example.test:8080/admin/metadata?mount=%2Fstation&mode=updinfo&song=START_AD_BREAK", sent!.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", sent.Headers.Authorization!.Scheme);
    }

    // Three seconds of a tone, then nothing new: like the live feed of a quiet program.
    private static BufferedWaveProvider Feed()
    {
        var feed = new BufferedWaveProvider(ProgramOutput.Format, TimeSpan.FromSeconds(10)) { ReadFully = false };
        var tone = new SignalGenerator(44100, 2) { Frequency = 440, Gain = 0.2 };
        var samples = new float[44100 * 2 * 3];
        tone.Read(samples);
        feed.AddSamples(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        return feed;
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream network)
    {
        var header = new StringBuilder();
        var buffer = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n") && await network.ReadAsync(buffer) == 1)
            header.Append((char)buffer[0]);
        return header.ToString();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
