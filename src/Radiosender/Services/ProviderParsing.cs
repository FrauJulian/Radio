using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radiosender.Services;

/// <summary>Helpers for JSON-based music providers: fetching, and reading licenses and durations.</summary>
internal static partial class ProviderParsing
{
    /// <summary>Fetches <paramref name="url"/> and parses it as JSON, whatever content type the server names.</summary>
    /// <exception cref="MusicProviderException">The request failed or the answer is not JSON.</exception>
    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string provider, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MusicProviderException($"{provider} hat HTTP {(int)response.StatusCode} geliefert.");
            // Parsed without looking at the content type: not every API labels its JSON as such.
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception e) when (e is not MusicProviderException && !cancellationToken.IsCancellationRequested)
        {
            throw e switch
            {
                OperationCanceledException => new MusicProviderException($"{provider}-Suche fehlgeschlagen: Zeitüberschreitung der Anfrage.", e),
                JsonException => new MusicProviderException($"{provider}-Suche fehlgeschlagen: unerwartete Antwort.", e),
                _ => new MusicProviderException($"{provider}-Suche fehlgeschlagen: {e.Message}", e),
            };
        }
    }

    /// <summary>
    /// The property as text: a string as it is, a number as its digits, and the first entry of a list
    /// (the Internet Archive sends one creator as a string and several as a list). Otherwise <c>null</c>.
    /// </summary>
    public static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Array)
            value = value.EnumerateArray().FirstOrDefault();
        var text = value.ValueKind switch
        {
            JsonValueKind.String => System.Net.WebUtility.HtmlDecode(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Readable name for a Creative Commons license URL, e.g. "CC BY-NC 3.0"; <c>null</c> for anything else.</summary>
    public static string? License(string? url)
    {
        if (url is null)
            return null;
        if (LicensePattern().Match(url) is { Success: true } license)
            return $"CC {license.Groups[1].Value.ToUpperInvariant()} {license.Groups[2].Value}";
        if (PublicDomainPattern().Match(url) is { Success: true } publicDomain)
            return publicDomain.Groups[1].Value == "zero" ? $"CC0 {publicDomain.Groups[2].Value}" : $"Public Domain Mark {publicDomain.Groups[2].Value}";
        return null;
    }

    /// <summary>Reads "2:36", "05:32", "1:02:03" or seconds such as "3808.26"; <c>null</c> when it is none of them.</summary>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Trim().Split(':');
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
                return null;
            seconds = seconds * 60 + value;
        }
        // An upper bound keeps a nonsense value from overflowing TimeSpan.
        return parts.Length <= 3 && seconds is > 0 and < 86400 ? TimeSpan.FromSeconds(seconds) : null;
    }

    [GeneratedRegex(@"creativecommons\.org/licenses/([a-z-]+)/(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex LicensePattern();

    [GeneratedRegex(@"creativecommons\.org/publicdomain/(zero|mark)/(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicDomainPattern();
}
