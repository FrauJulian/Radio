using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Radiosender.Models;

namespace Radiosender.Services.Providers;

public class OpenverseProvider(HttpClient http) : IMusicProvider
{
    private const string SearchUrl = "https://api.openverse.org/v1/audio/";
    private const int PageSize = 20; // anonymous requests may ask for at most 20 results per page

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Name => "Openverse";

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&page_size={PageSize}";
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MusicProviderException("Openverse-Limit erreicht (anonym: 20 Anfragen pro Minute, 200 pro Tag). Bitte später erneut versuchen.");
            if (!response.IsSuccessStatusCode)
                throw new MusicProviderException($"Openverse hat HTTP {(int)response.StatusCode} geliefert.");
            var page = await response.Content.ReadFromJsonAsync<SearchPage>(JsonOptions, cancellationToken);
            return page?.Results?.Select(result => result.ToMusicTrack()).ToList() ?? [];
        }
        catch (Exception e) when (e is not MusicProviderException && !cancellationToken.IsCancellationRequested)
        {
            throw e switch
            {
                OperationCanceledException => new MusicProviderException("Openverse-Suche fehlgeschlagen: Zeitüberschreitung der Anfrage.", e),
                JsonException => new MusicProviderException("Openverse-Suche fehlgeschlagen: unerwartete Antwort.", e),
                _ => new MusicProviderException($"Openverse-Suche fehlgeschlagen: {e.Message}", e),
            };
        }
    }

    private sealed record SearchPage(List<OpenverseTrack>? Results);
}
