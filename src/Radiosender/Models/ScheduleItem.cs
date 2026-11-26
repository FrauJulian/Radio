using Radiosender.Services.Localization;

namespace Radiosender.Models;

public record ScheduleItem(ScheduleItemKind Kind, string Title, DateTime Start, TimeSpan Duration, Song? Song = null)
{
    public DateTime End => Start + Duration;

    public string KindName => Loc.T($"Kind_{Kind}");
}
