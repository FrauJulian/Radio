namespace Radiosender.Models;

public record ScheduleItem(ScheduleItemKind Kind, string Title, DateTime Start, TimeSpan Duration, Song? Song = null)
{
    public DateTime End => Start + Duration;

    public string KindName => Kind switch
    {
        ScheduleItemKind.News => "Nachrichten",
        ScheduleItemKind.Ad => "Werbung",
        ScheduleItemKind.Moderator => "Moderation",
        _ => "Song",
    };
}
