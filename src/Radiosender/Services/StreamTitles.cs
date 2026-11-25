using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>
/// The stream title for each item. laut.fm starts its ad break when the title is "START_AD_BREAK" and
/// wants that at most once in 15 minutes, so later ad items within that time keep their own title.
/// </summary>
public class StreamTitles(Func<DateTime> now)
{
    public const string AdBreak = "START_AD_BREAK";
    public static readonly TimeSpan MinAdBreakSpacing = TimeSpan.FromMinutes(15);

    private DateTime? _lastAdBreak;

    public string For(ScheduleItem item)
    {
        if (item.Kind == ScheduleItemKind.Ad)
        {
            var time = now();
            if (_lastAdBreak is not { } last || time - last >= MinAdBreakSpacing)
            {
                _lastAdBreak = time;
                return AdBreak;
            }
            return item.Title;
        }
        return item.Song is { } song ? $"{song.Artist} - {song.Title}" : item.Title;
    }
}
