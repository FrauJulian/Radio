using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radiosender.Services.Providers;

internal static partial class ProviderParsing
{
    /// <exception cref="MusicProviderException">Request failed or answer is not JSON.</exception>
    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string provider, CancellationToken cancellationToken, string? bearerToken = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearerToken is not null)
                request.Headers.Authorization = new("Bearer", bearerToken);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && bearerToken is not null)
                throw new MusicProviderException($"{provider} hat die Anmeldung abgelehnt. Den Token in den Einstellungen prüfen.");
            if (!response.IsSuccessStatusCode)
                throw new MusicProviderException($"{provider} hat HTTP {(int)response.StatusCode} geliefert.");
            // Content type ignored: not every API labels JSON.
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

    // String, number digits, or first array entry (Archive: one creator as string, several as list).
    public static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Array)
            value = value.EnumerateArray().FirstOrDefault();
        var text = value.ValueKind switch
        {
            JsonValueKind.String => WebUtility.HtmlDecode(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

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

    // "2:36", "1:02:03" or seconds "3808.26".
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
        // Upper bound: no TimeSpan overflow on nonsense.
        return parts.Length <= 3 && seconds is > 0 and < 86400 ? TimeSpan.FromSeconds(seconds) : null;
    }

    [GeneratedRegex(@"creativecommons\.org/licenses/([a-z-]+)/(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex LicensePattern();

    [GeneratedRegex(@"creativecommons\.org/publicdomain/(zero|mark)/(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicDomainPattern();
}
