using Radiosender.Models;

namespace Radiosender.Services.Streaming;

// laut.fm: title AdBreak starts an ad break, max once per 15 min; later ads keep their own title.
public class StreamTitles(Func<DateTime> now)
{
    public const string AdBreak = "START_AD_BREAK";
    private static readonly TimeSpan MinAdBreakSpacing = TimeSpan.FromMinutes(15);

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
