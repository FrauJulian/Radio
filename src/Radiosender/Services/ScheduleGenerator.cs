using Radiosender.Models;

namespace Radiosender.Services;

public record ScheduleOptions
{
    public TimeSpan NewsDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper limit for ads and moderator talk together, per hour.</summary>
    public TimeSpan MaxFillerPerHour { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Ads and moderator talk planned at least per half hour, so the music is broken up.</summary>
    public TimeSpan MinFillerPerHalfHour { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Longest run of consecutive ad items. Must be below 20 minutes.</summary>
    public TimeSpan MaxAdBlock { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Random song picks tried per half hour before giving up.</summary>
    public int Attempts { get; init; } = 1000;
}

public class ScheduleException(string message) : Exception(message);

/// <summary>
/// Builds the plan in half-hour slots: news at :00 and :30, then random songs, with ads and
/// moderator talk sized so that the last song of each slot ends exactly when the next news starts.
/// </summary>
public class ScheduleGenerator(ScheduleOptions options, Random random)
{
    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    public ScheduleGenerator() : this(new ScheduleOptions(), Random.Shared)
    {
    }

    public IReadOnlyList<ScheduleItem> Generate(DateTime start, int hours, IReadOnlyList<Song> songs)
    {
        if (hours < 1)
            throw new ArgumentOutOfRangeException(nameof(hours), "At least one full hour is required.");
        if (start.Minute != 0 || start.Second != 0 || start.Millisecond != 0 || start.Microsecond != 0)
            throw new ArgumentException("The schedule must start at a full hour.", nameof(start));
        if (options.MaxAdBlock <= TimeSpan.Zero || options.MaxAdBlock >= TimeSpan.FromMinutes(20))
            throw new ArgumentException("MaxAdBlock must be between 0 and 20 minutes.", nameof(options));

        var playable = songs.Where(s => s.Duration > TimeSpan.Zero).ToList();
        if (playable.Count == 0)
            throw new ScheduleException("The music library has no songs to plan with.");

        var items = new List<ScheduleItem>();
        for (var slotStart = start; slotStart < start.AddHours(hours); slotStart += HalfHour)
        {
            items.Add(new ScheduleItem(ScheduleItemKind.News, "News", slotStart, options.NewsDuration));
            items.AddRange(BuildSlot(slotStart + options.NewsDuration, HalfHour - options.NewsDuration, playable));
        }
        return items;
    }

    private List<ScheduleItem> BuildSlot(DateTime slotStart, TimeSpan length, List<Song> songs)
    {
        // Half of the hourly limit per slot keeps every hour within MaxFillerPerHour.
        var maxFiller = options.MaxFillerPerHour / 2;
        var minFiller = options.MinFillerPerHalfHour < maxFiller ? options.MinFillerPerHalfHour : maxFiller;

        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            var picked = PickSongs(length - minFiller, songs);
            var gap = length - Sum(picked);
            if (picked.Count > 0 && gap <= maxFiller)
                return Arrange(slotStart, picked, BuildFiller(gap));
        }

        throw new ScheduleException(
            $"No combination of songs fills the slot at {slotStart:HH:mm} so that the last song ends on time. Add more songs, especially shorter ones.");
    }

    /// <summary>
    /// Hot swap: puts <paramref name="requested"/> at <paramref name="index"/> in place of that item and
    /// rebuilds the rest of its half-hour slot, so the last song still ends exactly when the next news starts.
    /// Items before <paramref name="index"/> and all later slots stay as they are.
    /// </summary>
    /// <exception cref="ScheduleException">The song does not fit before the next news, or the rest cannot be filled.</exception>
    public IReadOnlyList<ScheduleItem> Replace(IReadOnlyList<ScheduleItem> plan, int index, Song requested, IReadOnlyList<Song> songs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, plan.Count);
        if (plan[index].Kind == ScheduleItemKind.News)
            throw new ScheduleException("News cannot be replaced.");
        if (requested.Duration <= TimeSpan.Zero)
            throw new ScheduleException("The requested song has no duration.");

        var slotStartIndex = LastIndexOf(plan, index, ScheduleItemKind.News); // -1 if the plan does not start with news
        var nextNewsIndex = IndexOf(plan, index, ScheduleItemKind.News);
        var slotEnd = nextNewsIndex < 0 ? plan[^1].End : plan[nextNewsIndex].Start;
        var start = plan[index].Start;
        var length = slotEnd - start;

        if (requested.Duration > length)
            throw new ScheduleException(
                $"\"{requested.Title}\" ({requested.Duration:m\\:ss}) does not fit before the news at {slotEnd:HH:mm}; only {length:m\\:ss} are left. Choose an earlier item.");

        var prefix = plan.Take(index).ToList();
        var prefixFiller = Sum(prefix.Skip(slotStartIndex + 1).Where(i => i.Kind is ScheduleItemKind.Ad or ScheduleItemKind.Moderator));
        var maxGap = options.MaxFillerPerHour / 2 - prefixFiller;
        var playable = songs.Where(s => s.Duration > TimeSpan.Zero).ToList();
        var startWithAd = prefix.Count == 0 || prefix[^1].Kind != ScheduleItemKind.Ad;

        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            List<Song> picked = [requested, .. PickSongs(length - requested.Duration, playable, requested)];
            var gap = length - Sum(picked);
            if (gap <= maxGap)
            {
                IEnumerable<ScheduleItem> rest = nextNewsIndex < 0 ? [] : plan.Skip(nextNewsIndex);
                return [.. prefix, .. Arrange(start, picked, BuildFiller(gap, startWithAd)), .. rest];
            }
        }

        throw new ScheduleException(
            $"After \"{requested.Title}\" the slot up to {slotEnd:HH:mm} cannot be filled without exceeding the ad limit. Choose another item or song.");
    }

    private static int IndexOf(IReadOnlyList<ScheduleItem> plan, int from, ScheduleItemKind kind)
    {
        for (var i = from; i < plan.Count; i++)
            if (plan[i].Kind == kind) return i;
        return -1;
    }

    private static int LastIndexOf(IReadOnlyList<ScheduleItem> plan, int before, ScheduleItemKind kind)
    {
        for (var i = before - 1; i >= 0; i--)
            if (plan[i].Kind == kind) return i;
        return -1;
    }

    private List<Song> PickSongs(TimeSpan budget, List<Song> songs, Song? previous = null)
    {
        var picked = new List<Song>();
        var left = budget;
        while (true)
        {
            var last = picked.Count > 0 ? picked[^1] : previous;
            var fitting = songs.Where(s => s.Duration <= left && (last is null || s.Id != last.Id || songs.Count == 1)).ToList();
            if (fitting.Count == 0)
                return picked;
            var song = fitting[random.Next(fitting.Count)];
            picked.Add(song);
            left -= song.Duration;
        }
    }

    // Alternates ad and moderator items, so ads never run back to back.
    private List<(ScheduleItemKind Kind, string Title, TimeSpan Duration)> BuildFiller(TimeSpan gap, bool startWithAd = true)
    {
        var filler = new List<(ScheduleItemKind, string, TimeSpan)>();
        var nextIsAd = startWithAd;
        while (gap > TimeSpan.Zero)
        {
            (ScheduleItemKind kind, string title, TimeSpan duration) = nextIsAd
                ? Pick(PlaceholderContent.AdSpots, a => (ScheduleItemKind.Ad, a.Title, Min(a.Duration, options.MaxAdBlock)))
                : Pick(PlaceholderContent.ModeratorSegments, m => (ScheduleItemKind.Moderator, m.Title, m.Duration));
            duration = Min(duration, gap); // shortcut: the last placeholder is cut to fit; fine while they have no audio
            filler.Add((kind, title, duration));
            gap -= duration;
            nextIsAd = !nextIsAd;
        }
        return filler;
    }

    // Spreads the filler between songs; the slot always ends with a song.
    private static List<ScheduleItem> Arrange(DateTime start, List<Song> songs, List<(ScheduleItemKind Kind, string Title, TimeSpan Duration)> filler)
    {
        var items = new List<ScheduleItem>();
        var time = start;
        var fillerIndex = 0;
        for (var i = 0; i < songs.Count; i++)
        {
            // Filler goes before song i; the share grows with i so it ends up spread over the slot.
            var until = filler.Count * (i + 1) / songs.Count;
            for (; fillerIndex < until; fillerIndex++)
            {
                var f = filler[fillerIndex];
                items.Add(new ScheduleItem(f.Kind, f.Title, time, f.Duration));
                time += f.Duration;
            }
            var title = string.IsNullOrWhiteSpace(songs[i].Artist) ? songs[i].Title : $"{songs[i].Artist} - {songs[i].Title}";
            items.Add(new ScheduleItem(ScheduleItemKind.Song, title, time, songs[i].Duration, songs[i]));
            time += songs[i].Duration;
        }
        return items;
    }

    private T Pick<TSource, T>(IReadOnlyList<TSource> source, Func<TSource, T> map) => map(source[random.Next(source.Count)]);

    private static TimeSpan Sum(IEnumerable<Song> songs) => songs.Aggregate(TimeSpan.Zero, (total, s) => total + s.Duration);

    private static TimeSpan Sum(IEnumerable<ScheduleItem> items) => items.Aggregate(TimeSpan.Zero, (total, i) => total + i.Duration);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
