namespace Radiosender.Models;

public enum SongSource
{
    Upload,

    /// <summary>Downloaded from a music provider such as Openverse.</summary>
    Online,
}

public class Song
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string FilePath { get; set; } = "";
    public SongSource Source { get; set; }
    public string? License { get; set; }
    public string? Attribution { get; set; }

    /// <summary>Id of the provider result the song was imported from (<see cref="MusicTrack.Id"/>), so it is not imported twice.</summary>
    public string? ExternalId { get; set; }
}
