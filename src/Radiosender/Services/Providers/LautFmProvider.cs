using System.Net.Http;
using System.Text.Json;
using Radiosender.Models;
using Radiosender.Services.Settings;

namespace Radiosender.Services.Providers;

// Unofficial Radioadmin API with the user's token; may change without notice.
public class LautFmProvider(HttpClient http, Func<AppSettings> settings) : IMusicProvider
{
    private const string ApiBase = "https://api.radioadmin.laut.fm/";

    // Track search has its own host: "searchHost" in radioadmin.laut.fm/client/_/config.json.
    private const string SearchBase = "https://search.api.radioadmin.laut.fm/";

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

        // API filters one field per request: title and artist separately.
        var url = $"{SearchBase}stations/{Uri.EscapeDataString(station)}/tracks?";
        var results = await Task.WhenAll(
            SearchFieldAsync($"{url}title={Uri.EscapeDataString(words)}", current.LautFmToken, cancellationToken),
            SearchFieldAsync($"{url}artist={Uri.EscapeDataString(words)}", current.LautFmToken, cancellationToken));
        return [.. results.SelectMany(tracks => tracks).DistinctBy(track => track.Id)];
    }

    private async Task<List<MusicTrack>> SearchFieldAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var json = await ProviderParsing.GetJsonAsync(http, url, Name, cancellationToken, token);
        if (!json.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new MusicProviderException($"{Name}-Suche fehlgeschlagen: unerwartete Antwort.");

        List<MusicTrack> result = [];
        foreach (var track in tracks.EnumerateArray())
        {
            if (ProviderParsing.Text(track, "id") is not { } id
                || ProviderParsing.Text(track, "title") is not { } title
                || PrelistenUrl(track) is not { } audio)
                continue;
            result.Add(new MusicTrack
            {
                Id = $"lautfm:{id}",
                Title = title,
                Creator = ProviderParsing.Text(track, "artist"),
                Url = audio,
                FileType = "mp3",
                License = "laut.fm (nur für laut.fm)",
                Duration = ProviderParsing.Duration(ProviderParsing.Text(track, "duration")),
            });
        }
        return result;
    }

    // Radioadmin player link, no login; relative to ApiBase; https only.
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
