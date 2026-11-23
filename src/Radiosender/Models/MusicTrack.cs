namespace Radiosender.Models;

/// <summary>One search result of a music provider, in the shape the import works with.</summary>
public record MusicTrack
{
    /// <summary>
    /// Identifies the track so it is not imported twice. Must be unique across all providers: a new provider
    /// prefixes its ids with its own name. Empty when the provider has no id.
    /// </summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string? Creator { get; init; }

    /// <summary>Direct https link to the audio file.</summary>
    public string Url { get; init; } = "";

    /// <summary>File extension without the dot, e.g. "mp3"; unknown ones are saved as mp3.</summary>
    public string? FileType { get; init; }

    /// <summary>Readable license name, for example "CC BY 4.0".</summary>
    public string License { get; init; } = "";

    public string? Attribution { get; init; }

    /// <summary>Length of the track; <c>null</c> when the provider does not know it.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Songs need a duration for scheduling, so results without one cannot be imported.</summary>
    public bool CanImport => Duration is not null;

    public string DurationText => Duration?.ToString(@"m\:ss") ?? "unbekannt";
}
