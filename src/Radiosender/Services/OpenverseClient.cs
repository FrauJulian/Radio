using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>Error with a message that can be shown to the user as is.</summary>
public class OpenverseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Anonymous client for the Openverse audio search API and for downloading results.</summary>
public class OpenverseClient(HttpClient http, string musicFolder)
{
    public static string DefaultMusicFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "Music");

    public const long MaxDownloadBytes = 50 * 1024 * 1024;

    private const string SearchUrl = "https://api.openverse.org/v1/audio/";
    private const int PageSize = 20; // anonymous requests may ask for at most 20 results per page

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HashSet<string> KnownExtensions = ["mp3", "ogg", "oga", "wav", "flac", "m4a", "aac", "opus"];

    /// <summary>Limit for the whole download; <see cref="HttpClient.Timeout"/> only covers the response headers here.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public static OpenverseClient CreateDefault()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Radiosender/1.0");
        return new OpenverseClient(http, DefaultMusicFolder);
    }

    public async Task<IReadOnlyList<OpenverseTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&page_size={PageSize}";
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            EnsureSuccess(response);
            var page = await response.Content.ReadFromJsonAsync<SearchPage>(JsonOptions, cancellationToken);
            return page?.Results ?? [];
        }
        catch (Exception e) when (e is not OpenverseException && !cancellationToken.IsCancellationRequested)
        {
            throw Wrap(e, "Openverse search failed");
        }
    }

    /// <summary>
    /// Downloads <see cref="OpenverseTrack.Url"/> into the music folder under a new, unique file name.
    /// On any failure no file is left behind.
    /// </summary>
    /// <returns>Full path of the downloaded file.</returns>
    public async Task<string> DownloadAsync(OpenverseTrack track, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new OpenverseException("Only https downloads are allowed.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        string? path = null;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                throw new OpenverseException("The file is larger than 50 MB.");

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
                    throw new OpenverseException("The file is larger than 50 MB.");
                await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            if (total == 0)
                throw new OpenverseException("The download was empty.");
            return path;
        }
        catch (Exception e)
        {
            // "await using" in the try block has already closed the file here.
            if (path is not null)
                File.Delete(path);
            if (e is OpenverseException || cancellationToken.IsCancellationRequested)
                throw;
            throw Wrap(e, "Download failed");
        }
    }

    private FileStream CreateUniqueFile(OpenverseTrack track, out string path)
    {
        var name = string.Join("_", $"{track.Creator} - {track.Title}".Split(Path.GetInvalidFileNameChars())).Trim(' ', '-', '.');
        if (name.Length > 100) name = name[..100];
        if (name.Length == 0) name = "openverse";
        var type = track.FileType?.ToLowerInvariant();
        var extension = type == "mp32" ? "mp3" : type is not null && KnownExtensions.Contains(type) ? type : "mp3";

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(musicFolder, i == 1 ? $"{name}.{extension}" : $"{name} ({i}).{extension}");
            if (File.Exists(candidate)) continue;
            // CreateNew never overwrites; path is only reported once this call owns the file.
            var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            path = candidate;
            return stream;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new OpenverseException("Openverse rate limit reached (anonymous: 20 requests per minute, 200 per day). Please try again later.");
        if (!response.IsSuccessStatusCode)
            throw new OpenverseException($"Openverse returned HTTP {(int)response.StatusCode}.");
    }

    private static OpenverseException Wrap(Exception e, string action) => e switch
    {
        OperationCanceledException => new OpenverseException($"{action}: the request timed out.", e),
        JsonException => new OpenverseException($"{action}: unexpected response.", e),
        _ => new OpenverseException($"{action}: {e.Message}", e),
    };

    private sealed record SearchPage(List<OpenverseTrack>? Results);
}
