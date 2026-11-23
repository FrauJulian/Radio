namespace Radiosender.Models;

public enum SongSource
{
    Upload,
    Openverse,
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

    /// <summary>Id of the Openverse result the song was imported from, so it is not imported twice.</summary>
    public string? OpenverseId { get; set; }
}
