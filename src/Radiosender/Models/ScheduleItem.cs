namespace Radiosender.Models;

public enum ScheduleItemKind
{
    Song,
    News,
    Ad,
    Moderator,
}

/// <summary>One entry of the broadcast plan. <see cref="Song"/> is set only for <see cref="ScheduleItemKind.Song"/>.</summary>
public record ScheduleItem(ScheduleItemKind Kind, string Title, DateTime Start, TimeSpan Duration, Song? Song = null)
{
    public DateTime End => Start + Duration;

    /// <summary>The kind as the user reads it.</summary>
    public string KindName => Kind switch
    {
        ScheduleItemKind.News => "Nachrichten",
        ScheduleItemKind.Ad => "Werbung",
        ScheduleItemKind.Moderator => "Moderation",
        _ => "Song",
    };
}
