using System.IO;
using System.Net.Http;
using Radiosender.Models;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Providers;

public class TrackDownloader(HttpClient http, string musicFolder)
{
    public static string DefaultMusicFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "Music");

    public const long MaxDownloadBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> KnownExtensions = ["mp3", "ogg", "oga", "wav", "flac", "m4a", "aac", "opus"];

    // Whole download; HttpClient.Timeout only covers headers (ResponseHeadersRead).
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    /// <exception cref="MusicProviderException">Download failed or file not acceptable.</exception>
    public async Task<string> DownloadAsync(MusicTrack track, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new MusicProviderException(Loc.T("Provider_HttpsOnly"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        string? path = null;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new MusicProviderException(Loc.F("Provider_ServerHttp", (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                throw new MusicProviderException(Loc.T("Provider_TooLarge"));

            Directory.CreateDirectory(musicFolder);
            await using var file = CreateUniqueFile(track, out path);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                    throw new MusicProviderException(Loc.T("Provider_TooLarge"));
                await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            if (total == 0)
                throw new MusicProviderException(Loc.T("Provider_EmptyDownload"));
            return path;
        }
        catch (Exception e)
        {
            // await using already closed the file.
            if (path is not null)
                File.Delete(path);
            if (e is MusicProviderException || cancellationToken.IsCancellationRequested)
                throw;
            throw new MusicProviderException(e is OperationCanceledException
                ? Loc.T("Provider_DownloadTimeout")
                : Loc.F("Provider_DownloadFailed", e.Message), e);
        }
    }

    private FileStream CreateUniqueFile(MusicTrack track, out string path)
    {
        var name = string.Join("_", $"{track.Creator} - {track.Title}".Split(Path.GetInvalidFileNameChars())).Trim(' ', '-', '.');
        if (name.Length > 100) name = name[..100];
        if (name.Length == 0) name = "track";
        var type = track.FileType?.ToLowerInvariant();
        var extension = type is not null && KnownExtensions.Contains(type) ? type : "mp3";

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(musicFolder, i == 1 ? $"{name}.{extension}" : $"{name} ({i}).{extension}");
            if (File.Exists(candidate)) continue;
            // CreateNew never overwrites; path set only once owned.
            var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            path = candidate;
            return stream;
        }
    }
}
