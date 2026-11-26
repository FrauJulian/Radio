using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Radiosender.Models;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Providers;

public class OpenverseProvider(HttpClient http) : IMusicProvider
{
    private const string SearchUrl = "https://api.openverse.org/v1/audio/";
    private const int PageSize = 20; // anonymous requests may ask for at most 20 results per page

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Name => "Openverse";

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, IReadOnlySet<string>? licenses = null, CancellationToken cancellationToken = default)
    {
        if (licenses is { Count: 0 })
            return [];
        var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&page_size={PageSize}";
        // Server filters: full page of matches instead of a thinned-out one.
        if (licenses is not null)
            url += "&license=" + Uri.EscapeDataString(string.Join(',', licenses.Order()));
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MusicProviderException(Loc.F("Provider_RateLimit", Name));
            if (!response.IsSuccessStatusCode)
                throw new MusicProviderException(Loc.F("Provider_HttpStatus", Name, (int)response.StatusCode));
            var page = await response.Content.ReadFromJsonAsync<SearchPage>(JsonOptions, cancellationToken);
            return page?.Results?.Select(result => result.ToMusicTrack()).ToList() ?? [];
        }
        catch (Exception e) when (e is not MusicProviderException && !cancellationToken.IsCancellationRequested)
        {
            throw e switch
            {
                OperationCanceledException => new MusicProviderException(Loc.F("Provider_SearchTimeout", Name), e),
                JsonException => new MusicProviderException(Loc.F("Provider_SearchUnexpected", Name), e),
                _ => new MusicProviderException(Loc.F("Provider_SearchError", Name, e.Message), e),
            };
        }
    }

    private sealed record SearchPage(List<OpenverseTrack>? Results);
}
