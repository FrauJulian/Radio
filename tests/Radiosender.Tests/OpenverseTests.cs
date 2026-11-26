using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Library;
using Radiosender.Services.Providers;

namespace Radiosender.Tests;

public sealed class OpenverseTests : IDisposable
{
    private const string SearchJson = """
        {"result_count":2,"page_count":1,"page_size":20,"page":1,"results":[
          {"id":"a","title":"Piano Melody","url":"https://cdn.example.org/a.mp3","creator":"benpm",
           "license":"by","license_version":"4.0","filetype":"mp3","duration":10193,
           "attribution":"\"Piano Melody\" by benpm is licensed under CC BY 4.0."},
          {"id":"b","title":"No Length","url":"https://cdn.example.org/b","creator":null,
           "license":"cc0","license_version":"1.0","filetype":null,"duration":null,"attribution":null}
        ]}
        """;

    private static readonly byte[] Audio = [0x49, 0x44, 0x33, 1, 2, 3, 4, 5];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));
    private string MusicFolder => Path.Combine(_directory, "Music");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle, TimeSpan? timeout = null) =>
        new(new FakeHandler(handle)) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };

    private static OpenverseProvider NewProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle, TimeSpan? timeout = null) =>
        new(Http(handle, timeout));

    private TrackDownloader NewClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) =>
        new(Http(handle), MusicFolder);

    private static OnlineImportController NewController(TrackDownloader downloader, SongRepository songs) =>
        new([], downloader, songs);

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, HttpContent? content = null) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = content ?? new ByteArrayContent([]) });

    private static MusicTrack Track(string url = "https://cdn.example.org/a.mp3") => new()
    {
        Id = "a",
        Title = "Piano Melody",
        Creator = "benpm",
        Url = url,
        FileType = "mp3",
        License = "CC BY 4.0",
        Duration = TimeSpan.FromMilliseconds(10193),
        Attribution = "\"Piano Melody\" by benpm is licensed under CC BY 4.0.",
    };

    private string[] MusicFiles() => Directory.Exists(MusicFolder) ? Directory.GetFiles(MusicFolder) : [];

    [Fact]
    public void Track_DecodesHtmlEntitiesInTexts()
    {
        var track = new OpenverseTrack { Title = "Rock &amp; Roll", Creator = "Angus &amp; Friends", Attribution = "&quot;Rock&quot; by Angus" };

        Assert.Equal("Rock & Roll", track.Title);
        Assert.Equal("Angus & Friends", track.Creator);
        Assert.Equal("\"Rock\" by Angus", track.Attribution);
    }

    [Fact]
    public void Track_MapsToTheNeutralShape()
    {
        var track = new OpenverseTrack { Id = "a", Title = "T", License = "by", LicenseVersion = "4.0", FileType = "MP32", DurationMs = 2000 }.ToMusicTrack();

        Assert.Equal("a", track.Id);
        Assert.Equal("CC BY 4.0", track.License);
        Assert.Equal("mp3", track.FileType);
        Assert.Equal(TimeSpan.FromSeconds(2), track.Duration);
    }

    [Fact]
    public void Controller_ListsTheRegisteredProviders()
    {
        var provider = NewProvider((_, _) => Respond(HttpStatusCode.OK));
        var controller = new OnlineImportController([provider], NewClient((_, _) => Respond(HttpStatusCode.OK)), new SongRepository(Path.Combine(_directory, "test.db")));

        Assert.Equal("Openverse", Assert.Single(controller.Providers).Name);
    }

    [Fact]
    public async Task Controller_SearchesAllProvidersInParallelAndYieldsEachWhenDone()
    {
        var slowGate = new TaskCompletionSource<IReadOnlyList<MusicTrack>>();
        var slow = new FakeProvider("Slow", (_, _) => slowGate.Task);
        var failing = new FakeProvider("Failing", (_, _) => throw new MusicProviderException("kaputt"));
        IReadOnlySet<string>? seenLicenses = null;
        var fast = new FakeProvider("Fast", (_, licenses) => { seenLicenses = licenses; return Task.FromResult<IReadOnlyList<MusicTrack>>([Track()]); });
        var controller = new OnlineImportController([slow, failing, fast], NewClient((_, _) => Respond(HttpStatusCode.OK)), new SongRepository(Path.Combine(_directory, "test.db")));
        var licenses = new HashSet<string> { "by" };

        List<ProviderResult> results = [];
        await foreach (var result in controller.SearchAllAsync("jazz", licenses))
        {
            results.Add(result);
            if (results.Count == 2)
                slowGate.SetResult([Track() with { Id = "slow" }]); // slow only finishes after the others were yielded
        }

        Assert.Equal(["Failing", "Fast", "Slow"], results.Select(r => r.Provider.Name).Order());
        Assert.Equal("Slow", results[2].Provider.Name);
        Assert.Equal("kaputt", Assert.Single(results, r => r.Error is not null).Error!.Message);
        Assert.Empty(results.Single(r => r.Provider == failing).Tracks);
        Assert.Single(results.Single(r => r.Provider == fast).Tracks);
        Assert.Same(licenses, seenLicenses);
    }

    [Fact]
    public async Task Search_ParsesResults()
    {
        Uri? requested = null;
        var client = NewProvider((request, _) =>
        {
            requested = request.RequestUri;
            return Respond(HttpStatusCode.OK, new StringContent(SearchJson, Encoding.UTF8, "application/json"));
        });

        var tracks = await client.SearchAsync("jazz piano");

        Assert.Equal("https://api.openverse.org/v1/audio/?q=jazz%20piano&page_size=20", requested!.AbsoluteUri);
        Assert.Equal(2, tracks.Count);
        var first = tracks[0];
        Assert.Equal("a", first.Id);
        Assert.Equal("Piano Melody", first.Title);
        Assert.Equal("benpm", first.Creator);
        Assert.Equal("https://cdn.example.org/a.mp3", first.Url);
        Assert.Equal("mp3", first.FileType);
        Assert.Equal(TimeSpan.FromMilliseconds(10193), first.Duration);
        Assert.Equal("CC BY 4.0", first.License);
        Assert.StartsWith("\"Piano Melody\" by benpm", first.Attribution);
        Assert.True(first.CanImport);
        Assert.False(tracks[1].CanImport);
        Assert.Equal("CC0 1.0", tracks[1].License);
    }

    [Fact]
    public async Task Search_SendsTheChosenLicenses()
    {
        var requested = new List<Uri>();
        var client = NewProvider((request, _) =>
        {
            requested.Add(request.RequestUri!);
            return Respond(HttpStatusCode.OK, new StringContent(SearchJson, Encoding.UTF8, "application/json"));
        });

        await client.SearchAsync("jazz", new HashSet<string> { "cc0", "by" });
        var none = await client.SearchAsync("jazz", new HashSet<string>());

        Assert.Equal("https://api.openverse.org/v1/audio/?q=jazz&page_size=20&license=by%2Ccc0", Assert.Single(requested).AbsoluteUri);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Search_RateLimited_ThrowsReadableError()
    {
        var client = NewProvider((_, _) => Respond(HttpStatusCode.TooManyRequests));

        var error = await Assert.ThrowsAsync<MusicProviderException>(() => client.SearchAsync("x"));

        Assert.Contains("Limit erreicht", error.Message);
    }

    [Fact]
    public async Task Search_Timeout_ThrowsReadableError()
    {
        var client = NewProvider(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }, TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<MusicProviderException>(() => client.SearchAsync("x"));

        Assert.Contains("Zeitüberschreitung", error.Message);
    }

    [Fact]
    public async Task Import_WritesFileAndDatabaseEntryWithLicense()
    {
        var client = NewClient((_, _) => Respond(HttpStatusCode.OK, new ByteArrayContent(Audio)));
        var songs = new SongRepository(Path.Combine(_directory, "test.db"));
        var controller = NewController(client, songs);

        var song = await controller.ImportAsync(Track());

        var stored = songs.GetById(song.Id)!;
        Assert.Equal(Path.Combine(MusicFolder, "benpm - Piano Melody.mp3"), stored.FilePath);
        Assert.Equal(Audio, File.ReadAllBytes(stored.FilePath));
        Assert.Equal(SongSource.Online, stored.Source);
        Assert.Equal("CC BY 4.0", stored.License);
        Assert.Equal(Track().Attribution, stored.Attribution);
        Assert.Equal(TimeSpan.FromMilliseconds(10193), stored.Duration);
        Assert.Equal("benpm", stored.Artist);
        Assert.Equal("a", stored.ExternalId);
        Assert.Contains("a", controller.ImportedIds());
    }

    [Fact]
    public async Task Download_NeverOverwritesExistingFile()
    {
        Directory.CreateDirectory(MusicFolder);
        var existing = Path.Combine(MusicFolder, "benpm - Piano Melody.mp3");
        File.WriteAllText(existing, "keep");
        var client = NewClient((_, _) => Respond(HttpStatusCode.OK, new ByteArrayContent(Audio)));

        var path = await client.DownloadAsync(Track());

        Assert.Equal(Path.Combine(MusicFolder, "benpm - Piano Melody (2).mp3"), path);
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Download_FailingMidStream_LeavesNoFile()
    {
        var client = NewClient((_, _) => Respond(HttpStatusCode.OK, new StreamContent(new FailingStream())));

        await Assert.ThrowsAsync<MusicProviderException>(() => client.DownloadAsync(Track()));

        Assert.Empty(MusicFiles());
    }

    [Fact]
    public async Task Download_HttpErrorOrTooLarge_LeavesNoFile()
    {
        var notFound = NewClient((_, _) => Respond(HttpStatusCode.NotFound));
        var tooLarge = NewClient((_, _) =>
        {
            var content = new ByteArrayContent(Audio);
            content.Headers.ContentLength = TrackDownloader.MaxDownloadBytes + 1;
            return Respond(HttpStatusCode.OK, content);
        });

        await Assert.ThrowsAsync<MusicProviderException>(() => notFound.DownloadAsync(Track()));
        await Assert.ThrowsAsync<MusicProviderException>(() => tooLarge.DownloadAsync(Track()));

        Assert.Empty(MusicFiles());
    }

    [Fact]
    public async Task Download_RejectsNonHttpsUrl()
    {
        var called = false;
        var client = NewClient((_, _) => { called = true; return Respond(HttpStatusCode.OK); });

        await Assert.ThrowsAsync<MusicProviderException>(() => client.DownloadAsync(Track("http://cdn.example.org/a.mp3")));

        Assert.False(called);
    }

    [Fact]
    public async Task Import_WithoutDuration_IsRejected()
    {
        var client = NewClient((_, _) => Respond(HttpStatusCode.OK, new ByteArrayContent(Audio)));
        var songs = new SongRepository(Path.Combine(_directory, "test.db"));
        var track = new MusicTrack { Title = "x", Url = "https://cdn.example.org/x.mp3", License = "CC BY 4.0" };

        await Assert.ThrowsAsync<MusicProviderException>(() => NewController(client, songs).ImportAsync(track));

        Assert.Empty(MusicFiles());
        Assert.Empty(songs.GetAll());
    }

    private sealed class FakeProvider(string name, Func<string, IReadOnlySet<string>?, Task<IReadOnlyList<MusicTrack>>> search) : IMusicProvider
    {
        public string Name => name;

        public Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, IReadOnlySet<string>? licenses = null, CancellationToken cancellationToken = default) =>
            search(query, licenses);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }

    /// <summary>Returns some bytes, then fails like a dropped connection.</summary>
    private sealed class FailingStream : MemoryStream
    {
        private bool _sent;

        public FailingStream() : base(Audio) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent)
                throw new IOException("connection reset");
            _sent = true;
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
    }

    [Fact]
    public async Task Import_TrackAlreadyInLibrary_IsRejectedWithoutDownload()
    {
        var downloads = 0;
        var client = NewClient((_, _) => { downloads++; return Respond(HttpStatusCode.OK, new ByteArrayContent(Audio)); });
        var songs = new SongRepository(Path.Combine(_directory, "test.db"));
        var controller = NewController(client, songs);
        await controller.ImportAsync(Track());

        var error = await Assert.ThrowsAsync<MusicProviderException>(() => controller.ImportAsync(Track()));

        Assert.Contains("bereits in der Bibliothek", error.Message);
        Assert.Equal(1, downloads);
        Assert.Single(songs.GetAll());
        Assert.Single(MusicFiles());
    }}
