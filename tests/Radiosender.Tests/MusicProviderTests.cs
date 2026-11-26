using System.Net;
using System.Text;
using Radiosender.Services.Providers;

namespace Radiosender.Tests;

/// <summary>The Internet Archive provider against canned answers in the shape the real API sends.</summary>
public class MusicProviderTests
{
    private const string ArchiveSearchJson = """
        {"response":{"numFound":3,"docs":[
          {"identifier":"dancing-in-the-dark","title":"Dancing in the Dark","creator":["Bill Carrothers","José Encarnación"],
           "licenseurl":"https://creativecommons.org/licenses/by-nc-nd/4.0/"},
          {"identifier":"broken-item","title":"Broken","licenseurl":"https://creativecommons.org/publicdomain/mark/1.0/"}
        ]}}
        """;

    private const string ArchiveMetadataJson = """
        {"metadata":{"identifier":"dancing-in-the-dark"},"files":[
          {"name":"cover.png","format":"PNG"},
          {"name":"01 just.mp3","format":"VBR MP3","length":"05:32","title":"Just in Time","creator":"Bill Carrothers"},
          {"name":"live/02 set.mp3","format":"VBR MP3","length":"3808.26"},
          {"name":"03 talk.mp3","format":"VBR MP3"},
          {"name":"01 just.flac","format":"24bit Flac","length":"05:32"}
        ]}
        """;

    private static HttpClient Http(Func<Uri, HttpResponseMessage> respond) => new(new FakeHandler(respond));

    private static HttpResponseMessage Json(string json, string contentType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, contentType) };

    [Fact]
    public async Task InternetArchive_TurnsTheMp3FilesOfEachItemIntoTracks()
    {
        var requests = new List<Uri>();
        var provider = new InternetArchiveProvider(Http(uri =>
        {
            lock (requests) requests.Add(uri);
            if (uri.AbsolutePath == "/advancedsearch.php") return Json(ArchiveSearchJson);
            return uri.AbsolutePath.EndsWith("/dancing-in-the-dark") ? Json(ArchiveMetadataJson) : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));

        var tracks = await provider.SearchAsync("jazz");

        // The item whose file list failed is left out; the search still succeeds.
        Assert.Equal(3, tracks.Count);
        var first = tracks[0];
        Assert.Equal("archive:dancing-in-the-dark/01 just.mp3", first.Id);
        Assert.Equal("Just in Time", first.Title);
        Assert.Equal("Bill Carrothers", first.Creator);
        Assert.Equal("https://archive.org/download/dancing-in-the-dark/01%20just.mp3", first.Url);
        Assert.Equal("CC BY-NC-ND 4.0", first.License);
        Assert.Equal(TimeSpan.FromSeconds(332), first.Duration);
        Assert.Contains("https://archive.org/details/dancing-in-the-dark", first.Attribution);
        // No title or creator on the file: file name and the item's first creator; seconds as length; folders kept in the link.
        Assert.Equal("02 set", tracks[1].Title);
        Assert.Equal("Bill Carrothers", tracks[1].Creator);
        Assert.Equal("https://archive.org/download/dancing-in-the-dark/live/02%20set.mp3", tracks[1].Url);
        Assert.Equal(TimeSpan.FromSeconds(3808.26), tracks[1].Duration);
        Assert.False(tracks[2].CanImport);
    }

    [Fact]
    public async Task InternetArchive_ReportsHttpErrorsAndBrokenAnswers()
    {
        var failing = new InternetArchiveProvider(Http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var broken = new InternetArchiveProvider(Http(_ => Json("<html>maintenance</html>", "text/html")));
        var wrongShape = new InternetArchiveProvider(Http(_ => Json("{}")));

        Assert.Contains("503", (await Assert.ThrowsAsync<MusicProviderException>(() => failing.SearchAsync("x"))).Message);
        await Assert.ThrowsAsync<MusicProviderException>(() => broken.SearchAsync("x"));
        await Assert.ThrowsAsync<MusicProviderException>(() => wrongShape.SearchAsync("x"));
    }

    [Fact]
    public async Task InternetArchive_KeepsQueryOperatorsOutOfTheFilter()
    {
        string? query = null;
        var provider = new InternetArchiveProvider(Http(uri =>
        {
            query = Uri.UnescapeDataString(uri.Query);
            return Json("""{"response":{"docs":[]}}""");
        }));

        Assert.Empty(await provider.SearchAsync("jazz) OR mediatype:(movies"));
        Assert.Contains("q=(jazz mediatype movies) AND mediatype:audio AND licenseurl:*creativecommons* AND format:(MP3)", query);

        query = null;
        Assert.Empty(await provider.SearchAsync(" ()* "));
        Assert.Null(query); // nothing left to search for, so no request
    }

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }
}
