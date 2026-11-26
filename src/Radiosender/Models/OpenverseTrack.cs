using System.Net;
using System.Text.Json.Serialization;

namespace Radiosender.Models;

// Openverse sends some texts HTML-encoded: decoded on init.
public class OpenverseTrack
{
    public string Id { get; init; } = "";
    public string Title { get; init => field = WebUtility.HtmlDecode(value); } = "";
    public string? Creator { get; init => field = WebUtility.HtmlDecode(value); }
    public string Url { get; init; } = "";
    [JsonPropertyName("filetype")]
    public string? FileType { get; init; }
    public string License { get; init; } = "";
    public string? LicenseVersion { get; init; }
    public string? Attribution { get; init => field = WebUtility.HtmlDecode(value); }

    [JsonPropertyName("duration")]
    public int? DurationMs { get; init; }

    [JsonIgnore]
    public TimeSpan? Duration => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs.Value) : null;

    private string LicenseName => License.ToLowerInvariant() switch
    {
        "cc0" => $"CC0 {LicenseVersion}".Trim(),
        "pdm" => $"Public Domain Mark {LicenseVersion}".Trim(),
        _ => $"CC {License.ToUpperInvariant()} {LicenseVersion}".Trim(),
    };

    // Openverse ids stay unprefixed, unlike other providers.
    public MusicTrack ToMusicTrack() => new()
    {
        Id = Id,
        Title = Title,
        Creator = Creator,
        Url = Url,
        // Openverse reports some MP3s as "mp32".
        FileType = FileType?.ToLowerInvariant() is "mp32" ? "mp3" : FileType,
        License = LicenseName,
        Attribution = Attribution,
        Duration = Duration,
    };
}
