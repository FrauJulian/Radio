using System.Text.Json.Serialization;

namespace Radiosender.Models;

/// <summary>One audio result of the Openverse search API (fields as documented at api.openverse.org/v1).</summary>
/// <remarks>Openverse sends some texts HTML-encoded (&amp;amp;), so the text fields decode on assignment.</remarks>
public class OpenverseTrack
{
    public string Id { get; init; } = "";
    public string Title { get; init => field = System.Net.WebUtility.HtmlDecode(value); } = "";
    public string? Creator { get; init => field = System.Net.WebUtility.HtmlDecode(value); }
    public string Url { get; init; } = "";
    [JsonPropertyName("filetype")]
    public string? FileType { get; init; }
    public string License { get; init; } = "";
    public string? LicenseVersion { get; init; }
    public string? Attribution { get; init => field = System.Net.WebUtility.HtmlDecode(value); }

    /// <summary>Length in milliseconds; Openverse leaves it empty for some results.</summary>
    [JsonPropertyName("duration")]
    public int? DurationMs { get; init; }

    [JsonIgnore]
    public TimeSpan? Duration => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs.Value) : null;

    /// <summary>Songs need a duration for scheduling, so results without one cannot be imported.</summary>
    public bool CanImport => Duration is not null;

    public string DurationText => Duration?.ToString(@"m\:ss") ?? "unbekannt";

    /// <summary>Readable license name, for example "CC BY 4.0", "CC0 1.0" or "Public Domain Mark 1.0".</summary>
    public string LicenseName => License.ToLowerInvariant() switch
    {
        "cc0" => $"CC0 {LicenseVersion}".Trim(),
        "pdm" => $"Public Domain Mark {LicenseVersion}".Trim(),
        _ => $"CC {License.ToUpperInvariant()} {LicenseVersion}".Trim(),
    };
}
