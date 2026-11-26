namespace Radiosender.Models;

public record MusicTrack
{
    // Unique across providers (prefix with provider name, Openverse unprefixed); empty if none.
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string? Creator { get; init; }

    public string Url { get; init; } = "";

    public string? FileType { get; init; }

    public string License { get; init; } = "";

    public string? Attribution { get; init; }

    public TimeSpan? Duration { get; init; }

    public bool CanImport => Duration is not null;

    public string DurationText => Duration?.ToString(@"m\:ss") ?? "unbekannt";
}
