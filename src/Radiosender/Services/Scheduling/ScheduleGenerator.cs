using Radiosender.Models;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Scheduling;

// Half-hour slots: news at :00/:30; last song ends exactly at next news; ads/talk fill the rest.
public class ScheduleGenerator(ScheduleOptions options, Random random)
{
    public ScheduleOptions Options { get; set; } = options;

    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan CloseEnough = TimeSpan.FromSeconds(1);

    public ScheduleGenerator() : this(new ScheduleOptions(), Random.Shared)
    {
    }

    public IReadOnlyList<ScheduleItem> Generate(DateTime start, int hours, IReadOnlyList<Song> songs)
    {
        var options = Options; // snapshot: Options can be swapped mid-plan
        if (hours < 1)
            throw new ArgumentOutOfRangeException(nameof(hours), "At least one full hour is required.");
        if (options.MaxAdBlock <= TimeSpan.Zero || options.MaxAdBlock >= TimeSpan.FromMinutes(20))
            throw new ArgumentException("MaxAdBlock must be between 0 and 20 minutes.", nameof(options));

        List<Song> playable = [.. songs.Where(s => s.Duration > TimeSpan.Zero)];
        if (playable.Count == 0)
            throw new ScheduleException(Loc.T("Scheduling_NoSongs"));

        List<ScheduleItem> items = [];
        var firstNews = new DateTime((start.Ticks + HalfHour.Ticks - 1) / HalfHour.Ticks * HalfHour.Ticks, start.Kind);
        if (firstNews > start)
            items.AddRange(BuildLeadIn(options, start, firstNews - start, playable));

        var adHalf = options.AdsPerHour / 2;
        var moderatorHalf = options.ModeratorPerHour / 2;
        // :00 slot over/under target; :30 slot compensates.
        var adOver = TimeSpan.Zero;
        var moderatorOver = TimeSpan.Zero;
        for (var slotStart = firstNews; slotStart < firstNews.AddHours(hours); slotStart += HalfHour)
        {
            if (slotStart.Minute == 0)
                adOver = moderatorOver = TimeSpan.Zero;

            var news = Min(options.NewsDuration, HalfHour);
            if (news > TimeSpan.Zero)
                items.Add(new ScheduleItem(ScheduleItemKind.News, "", slotStart, news));
            var slot = BuildSlot(options, slotStart + news, HalfHour - news,
                Max(adHalf - adOver, TimeSpan.Zero), Max(moderatorHalf - moderatorOver, TimeSpan.Zero), playable);
            items.AddRange(slot);

            adOver = Sum(slot.Where(i => i.Kind == ScheduleItemKind.Ad)) - adHalf;
            moderatorOver = Sum(slot.Where(i => i.Kind == ScheduleItemKind.Moderator)) - moderatorHalf;
        }
        return items;
    }

    private List<ScheduleItem> BuildLeadIn(ScheduleOptions options, DateTime start, TimeSpan length, List<Song> songs)
    {
        var share = length / HalfHour;
        try
        {
            return BuildSlot(options, start, length, options.AdsPerHour / 2 * share, options.ModeratorPerHour / 2 * share, songs);
        }
        catch (ScheduleException) when (length <= (options.AdsPerHour + options.ModeratorPerHour) / 2)
        {
            // Too short for any song: moderator fills.
            return [new ScheduleItem(ScheduleItemKind.Moderator, "", start, length)];
        }
    }

    private List<ScheduleItem> BuildSlot(
        ScheduleOptions options, DateTime slotStart, TimeSpan length, TimeSpan adTarget, TimeSpan moderatorTarget, List<Song> songs)
    {
        if (length <= TimeSpan.Zero)
            return [];
        var target = Min(adTarget + moderatorTarget, length);
        var (picked, gap) = PickBest(options, length, target, songs);
        if (picked.Count == 0 && target < length)
            throw new ScheduleException(
                Loc.F("Scheduling_NoCombination", slotStart));

        var (ads, moderator) = Split(options, gap, adTarget, moderatorTarget);
        return Arrange(options, slotStart, picked, ads, moderator, keepFirstSongFirst: false);
    }

    // Hot swap: rebuilds rest of the slot only; earlier items and later slots stay.
    /// <exception cref="ScheduleException">Song does not fit before next news.</exception>
    public IReadOnlyList<ScheduleItem> Replace(IReadOnlyList<ScheduleItem> plan, int index, Song requested, IReadOnlyList<Song> songs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, plan.Count);
        if (plan[index].Kind == ScheduleItemKind.News)
            throw new ScheduleException(Loc.T("Schedule_NewsNoReplace"));
        if (requested.Duration <= TimeSpan.Zero)
            throw new ScheduleException(Loc.T("Scheduling_NoDuration"));

        var slotStartIndex = LastIndexOf(plan, index, ScheduleItemKind.News); // -1 if the plan does not start with news
        var nextNewsIndex = IndexOf(plan, index, ScheduleItemKind.News);
        var slotEnd = nextNewsIndex < 0 ? plan[^1].End : plan[nextNewsIndex].Start;
        var start = plan[index].Start;
        var length = slotEnd - start;

        if (requested.Duration > length)
            throw new ScheduleException(
                Loc.F("Scheduling_DoesNotFit", requested.Title, requested.Duration, slotEnd, length));

        // Ads/talk already in this slot count against its targets.
        var options = Options;
        List<ScheduleItem> prefix = [.. plan.Take(index)];
        List<ScheduleItem> slotPrefix = [.. prefix.Skip(slotStartIndex + 1)];
        var adTarget = Max(options.AdsPerHour / 2 - Sum(slotPrefix.Where(i => i.Kind == ScheduleItemKind.Ad)), TimeSpan.Zero);
        var moderatorTarget = Max(options.ModeratorPerHour / 2 - Sum(slotPrefix.Where(i => i.Kind == ScheduleItemKind.Moderator)), TimeSpan.Zero);
        List<Song> playable = [.. songs.Where(s => s.Duration > TimeSpan.Zero)];

        var (picked, gap) = PickBest(options, length, Min(adTarget + moderatorTarget, length), playable, requested);
        var (ads, moderator) = Split(options, gap, adTarget, moderatorTarget);
        IEnumerable<ScheduleItem> rest = nextNewsIndex < 0 ? [] : plan.Skip(nextNewsIndex);
        return [.. prefix, .. Arrange(options, start, picked, ads, moderator, keepFirstSongFirst: true), .. rest];
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

    // Picks never exceed music budget, so gap >= target; smallest gap wins.
    private (List<Song> Songs, TimeSpan Gap) PickBest(
        ScheduleOptions options, TimeSpan length, TimeSpan target, List<Song> songs, Song? first = null)
    {
        var fixedTime = first?.Duration ?? TimeSpan.Zero;
        var budget = Max(length - fixedTime - target, TimeSpan.Zero);
        (List<Song> Songs, TimeSpan Gap)? best = null;
        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            var more = PickSongs(budget, songs, first);
            List<Song> picked = first is null ? more : [first, .. more];
            var gap = length - Sum(picked);
            if (best is null || gap < best.Value.Gap)
                best = (picked, gap);
            if (gap - target <= CloseEnough || budget == TimeSpan.Zero)
                break;
        }
        return best ?? ([], length);
    }

    private List<Song> PickSongs(TimeSpan budget, List<Song> songs, Song? previous = null)
    {
        List<Song> picked = [];
        var left = budget;
        while (true)
        {
            var last = picked.Count > 0 ? picked[^1] : previous;
            List<Song> fitting = [.. songs.Where(s => s.Duration <= left && (last is null || s.Id != last.Id || songs.Count == 1))];
            if (fitting.Count == 0)
                return picked;
            var song = fitting[random.Next(fitting.Count)];
            picked.Add(song);
            left -= song.Duration;
        }
    }

    // Gap split in target ratio; no target left: hourly ratio, else talk only.
    private static (TimeSpan Ads, TimeSpan Moderator) Split(ScheduleOptions options, TimeSpan gap, TimeSpan adTarget, TimeSpan moderatorTarget)
    {
        if (adTarget + moderatorTarget <= TimeSpan.Zero)
        {
            adTarget = options.AdsPerHour;
            moderatorTarget = options.ModeratorPerHour;
            if (adTarget + moderatorTarget <= TimeSpan.Zero)
                return (TimeSpan.Zero, gap);
        }
        var ads = TimeSpan.FromSeconds(Math.Round(gap.TotalSeconds * (adTarget / (adTarget + moderatorTarget))));
        ads = Min(ads, gap);
        return (ads, gap - ads);
    }

    // Breaks "moderator, ad, moderator, ad..." between songs; ads <= MaxAdBlock, never back to back; slot ends with a song.
    private static List<ScheduleItem> Arrange(
        ScheduleOptions options, DateTime start, List<Song> songs, TimeSpan ads, TimeSpan moderator, bool keepFirstSongFirst)
    {
        var adPieces = Pieces(ads, (int)Math.Ceiling(ads / options.MaxAdBlock));
        var moderatorPieces = Pieces(moderator, adPieces.Count > 0 ? adPieces.Count : (int)Math.Ceiling(moderator / TimeSpan.FromMinutes(5)));
        var pairs = Math.Max(adPieces.Count, moderatorPieces.Count);

        // Hot swap: requested song stays first, breaks start after it.
        var firstBreakSong = keepFirstSongFirst ? 1 : 0; // also when it is the only song: breaks then follow it
        var breakSlots = Math.Max(songs.Count - firstBreakSong, 1);
        var breaks = Math.Min(pairs, breakSlots);

        List<ScheduleItem> items = [];
        var time = start;
        void Add(ScheduleItemKind kind, TimeSpan duration, Song? song = null)
        {
            if (duration <= TimeSpan.Zero)
                return;
            var title = song is null ? "" : string.IsNullOrWhiteSpace(song.Artist) ? song.Title : $"{song.Artist} - {song.Title}";
            items.Add(new ScheduleItem(kind, title, time, duration, song));
            time += duration;
        }

        var pair = 0;
        void AddBreak(int number)
        {
            var until = number == breaks - 1 ? pairs : pairs * (number + 1) / breaks;
            for (; pair < until; pair++)
            {
                if (pair < moderatorPieces.Count) Add(ScheduleItemKind.Moderator, moderatorPieces[pair]);
                if (pair < adPieces.Count) Add(ScheduleItemKind.Ad, adPieces[pair]);
            }
        }

        var nextBreak = 0;
        for (var i = 0; i < songs.Count; i++)
        {
            while (nextBreak < breaks && i == firstBreakSong + nextBreak * breakSlots / breaks)
                AddBreak(nextBreak++);
            Add(ScheduleItemKind.Song, songs[i].Duration, songs[i]);
        }
        while (nextBreak < breaks)
            AddBreak(nextBreak++);
        return items;
    }

    // Whole-second pieces differing by <= 1 s, so none exceeds the limit; sub-second rest goes to last piece.
    private static List<TimeSpan> Pieces(TimeSpan total, int count)
    {
        if (total <= TimeSpan.Zero || count < 1)
            return [];
        List<TimeSpan> pieces = [];
        var seconds = (long)Math.Floor(total.TotalSeconds);
        for (var i = 0; i < count; i++)
            pieces.Add(TimeSpan.FromSeconds(seconds / count + (i < seconds % count ? 1 : 0)));
        pieces[^1] += total - TimeSpan.FromSeconds(seconds);
        return pieces;
    }

    private static TimeSpan Sum(IEnumerable<Song> songs) => songs.Aggregate(TimeSpan.Zero, (total, s) => total + s.Duration);

    private static TimeSpan Sum(IEnumerable<ScheduleItem> items) => items.Aggregate(TimeSpan.Zero, (total, i) => total + i.Duration);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
