using System.Net.Http;
using System.Text.Json;
using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>
/// Searches the music library of the user's laut.fm station, so laut.fm tracks can be imported next to
/// local songs. Uses the API behind Radioadmin with the user's login token. laut.fm does not publish
/// this API for other programs, so it can change without notice.
/// </summary>
public class LautFmProvider(HttpClient http, Func<AppSettings> settings) : IMusicProvider
{
    public const string ApiBase = "https://api.radioadmin.laut.fm/";

    public string Name => "laut.fm";

    public bool IsAvailable => settings().LautFmEnabled;

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var current = settings();
        var station = current.LautFmStationId.Trim();
        if (current.LautFmToken.Length == 0 || station.Length == 0)
            throw new MusicProviderException("Für die laut.fm-Bibliothek fehlen Stations-ID oder Token in den Einstellungen.");
        var words = query.Trim();
        if (words.Length == 0)
            return [];

        // The API filters one field at a time, so title and artist are two searches.
        var url = $"{ApiBase}stations/{Uri.EscapeDataString(station)}/tracks?";
        var results = await Task.WhenAll(
            SearchFieldAsync($"{url}title={Uri.EscapeDataString(words)}", current.LautFmToken, cancellationToken),
            SearchFieldAsync($"{url}artist={Uri.EscapeDataString(words)}", current.LautFmToken, cancellationToken));
        return results.SelectMany(tracks => tracks).DistinctBy(track => track.Id).ToList();
    }

    private async Task<List<MusicTrack>> SearchFieldAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var json = await ProviderParsing.GetJsonAsync(http, url, Name, cancellationToken, token);
        if (!json.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new MusicProviderException($"{Name}-Suche fehlgeschlagen: unerwartete Antwort.");

        var result = new List<MusicTrack>();
        foreach (var track in tracks.EnumerateArray())
        {
            if (ProviderParsing.Text(track, "id") is not { } id
                || ProviderParsing.Text(track, "title") is not { } title
                || PrelistenUrl(track) is not { } audio)
                continue;
            result.Add(new MusicTrack
            {
                // Prefixed, so the id cannot collide with one from another provider.
                Id = $"lautfm:{id}",
                Title = title,
                Creator = ProviderParsing.Text(track, "artist"),
                Url = audio,
                FileType = "mp3",
                // The station's tracks are licensed by laut.fm for broadcasting on laut.fm only.
                License = "laut.fm (nur für laut.fm)",
                Duration = ProviderParsing.Duration(ProviderParsing.Text(track, "duration")),
            });
        }
        return result;
    }

    // The link the Radioadmin player uses; it needs no login. Relative links are taken against the API.
    private static string? PrelistenUrl(JsonElement track)
    {
        if (!track.TryGetProperty("_links", out var links) || links.ValueKind != JsonValueKind.Object
            || !links.TryGetProperty("prelisten_authless", out var link)
            || ProviderParsing.Text(link, "href") is not { } href
            || !Uri.TryCreate(new Uri(ApiBase), href, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return uri.AbsoluteUri;
    }
}
