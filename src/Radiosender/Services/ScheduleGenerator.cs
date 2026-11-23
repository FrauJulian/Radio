using Radiosender.Models;

namespace Radiosender.Services;

public record ScheduleOptions
{
    public TimeSpan NewsDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Target for ads per hour. Met as closely as the song lengths allow.</summary>
    public TimeSpan AdsPerHour { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Target for moderator talk per hour. Met as closely as the song lengths allow.</summary>
    public TimeSpan ModeratorPerHour { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Longest single ad item; longer ad time is split and separated by moderator talk or songs.</summary>
    public TimeSpan MaxAdBlock { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Random song picks tried per half hour; the one closest to the targets wins.</summary>
    public int Attempts { get; init; } = 400;
}

public class ScheduleException(string message) : Exception(message);

/// <summary>
/// Builds the plan in half-hour slots: news at :00 and :30, then random songs with ads and moderator talk
/// in between. The last song of each slot ends exactly when the next news starts, so the ad and moderator
/// time is whatever the songs leave; the generator picks songs that leave as close to the targets as it can
/// and evens out the difference in the second half of the hour.
/// A start between two news times gets a lead-in without news up to the next :00 or :30.
/// </summary>
public class ScheduleGenerator(ScheduleOptions options, Random random)
{
    /// <summary>Can be replaced at any time, e.g. from the settings page; the next plan uses the new values.</summary>
    public ScheduleOptions Options { get; set; } = options;

    private static readonly TimeSpan HalfHour = TimeSpan.FromMinutes(30);

    // A pick that misses the target by this little is taken at once.
    private static readonly TimeSpan CloseEnough = TimeSpan.FromSeconds(1);

    public ScheduleGenerator() : this(new ScheduleOptions(), Random.Shared)
    {
    }

    /// <summary>
    /// Plans <paramref name="hours"/> full hours. They begin at <paramref name="start"/> if that is :00 or :30,
    /// otherwise at the next :00 or :30 after a lead-in of music.
    /// </summary>
    public IReadOnlyList<ScheduleItem> Generate(DateTime start, int hours, IReadOnlyList<Song> songs)
    {
        var options = Options; // one consistent set of values for the whole plan
        if (hours < 1)
            throw new ArgumentOutOfRangeException(nameof(hours), "At least one full hour is required.");
        if (options.MaxAdBlock <= TimeSpan.Zero || options.MaxAdBlock >= TimeSpan.FromMinutes(20))
            throw new ArgumentException("MaxAdBlock must be between 0 and 20 minutes.", nameof(options));

        var playable = songs.Where(s => s.Duration > TimeSpan.Zero).ToList();
        if (playable.Count == 0)
            throw new ScheduleException("Die Bibliothek enthält keine Songs zum Planen.");

        var items = new List<ScheduleItem>();
        var firstNews = new DateTime((start.Ticks + HalfHour.Ticks - 1) / HalfHour.Ticks * HalfHour.Ticks, start.Kind);
        if (firstNews > start)
            items.AddRange(BuildLeadIn(options, start, firstNews - start, playable));

        var adHalf = options.AdsPerHour / 2;
        var moderatorHalf = options.ModeratorPerHour / 2;
        // How far the :00 slot ended up over (or under) its half of the hour; the :30 slot makes up for it.
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
        // The targets shrink with the lead-in: half of a half hour gets half of the ads.
        var share = length / HalfHour;
        try
        {
            return BuildSlot(options, start, length, options.AdsPerHour / 2 * share, options.ModeratorPerHour / 2 * share, songs);
        }
        catch (ScheduleException) when (length <= (options.AdsPerHour + options.ModeratorPerHour) / 2)
        {
            // Too short for any song: the moderator talks until the news.
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
        // No song at all is only right when the targets leave no room for music.
        if (picked.Count == 0 && target < length)
            throw new ScheduleException(
                $"Keine Song-Kombination füllt den Abschnitt ab {slotStart:HH:mm} so, dass der letzte Song pünktlich endet. Mehr Songs hinzufügen, vor allem kürzere.");

        var (ads, moderator) = Split(options, gap, adTarget, moderatorTarget);
        return Arrange(options, slotStart, picked, ads, moderator, keepFirstSongFirst: false);
    }

    /// <summary>
    /// Hot swap: puts <paramref name="requested"/> at <paramref name="index"/> in place of that item and
    /// rebuilds the rest of its half-hour slot, so the last song still ends exactly when the next news starts.
    /// Items before <paramref name="index"/> and all later slots stay as they are.
    /// </summary>
    /// <exception cref="ScheduleException">The song does not fit before the next news.</exception>
    public IReadOnlyList<ScheduleItem> Replace(IReadOnlyList<ScheduleItem> plan, int index, Song requested, IReadOnlyList<Song> songs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, plan.Count);
        if (plan[index].Kind == ScheduleItemKind.News)
            throw new ScheduleException("Nachrichten können nicht ersetzt werden.");
        if (requested.Duration <= TimeSpan.Zero)
            throw new ScheduleException("Der gewünschte Song hat keine Dauer.");

        var slotStartIndex = LastIndexOf(plan, index, ScheduleItemKind.News); // -1 if the plan does not start with news
        var nextNewsIndex = IndexOf(plan, index, ScheduleItemKind.News);
        var slotEnd = nextNewsIndex < 0 ? plan[^1].End : plan[nextNewsIndex].Start;
        var start = plan[index].Start;
        var length = slotEnd - start;

        if (requested.Duration > length)
            throw new ScheduleException(
                $"\"{requested.Title}\" ({requested.Duration:m\\:ss}) passt nicht vor die Nachrichten um {slotEnd:HH:mm}; es sind nur noch {length:m\\:ss} übrig. Ein früheres Element wählen.");

        // What the slot has already spent on ads and moderator talk before the request counts against its targets.
        var options = Options;
        var prefix = plan.Take(index).ToList();
        var slotPrefix = prefix.Skip(slotStartIndex + 1).ToList();
        var adTarget = Max(options.AdsPerHour / 2 - Sum(slotPrefix.Where(i => i.Kind == ScheduleItemKind.Ad)), TimeSpan.Zero);
        var moderatorTarget = Max(options.ModeratorPerHour / 2 - Sum(slotPrefix.Where(i => i.Kind == ScheduleItemKind.Moderator)), TimeSpan.Zero);
        var playable = songs.Where(s => s.Duration > TimeSpan.Zero).ToList();

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

    // Tries random song picks and keeps the one whose leftover time is closest to the target for ads and
    // moderator talk. Picks never go over the music time, so the leftover is the target or more.
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

    // Shares the leftover time between ads and moderator talk in the ratio of their targets.
    private static (TimeSpan Ads, TimeSpan Moderator) Split(ScheduleOptions options, TimeSpan gap, TimeSpan adTarget, TimeSpan moderatorTarget)
    {
        if (adTarget + moderatorTarget <= TimeSpan.Zero)
        {
            // This slot has nothing left to aim for; fall back to the hourly ratio, or to talk alone.
            adTarget = options.AdsPerHour;
            moderatorTarget = options.ModeratorPerHour;
            if (adTarget + moderatorTarget <= TimeSpan.Zero)
                return (TimeSpan.Zero, gap);
        }
        var ads = TimeSpan.FromSeconds(Math.Round(gap.TotalSeconds * (adTarget / (adTarget + moderatorTarget))));
        ads = Min(ads, gap);
        return (ads, gap - ads);
    }

    // Lays out the slot: songs with breaks in between, each break "moderator, ad, moderator, ad ...".
    // Ads are cut into pieces of at most MaxAdBlock and never follow each other directly as long as there
    // is moderator talk or a song to put between them. The slot always ends with a song.
    private static List<ScheduleItem> Arrange(
        ScheduleOptions options, DateTime start, List<Song> songs, TimeSpan ads, TimeSpan moderator, bool keepFirstSongFirst)
    {
        var adPieces = Pieces(ads, (int)Math.Ceiling(ads / options.MaxAdBlock));
        // One moderator piece in front of every ad piece; without ads, one piece per five minutes of talk.
        var moderatorPieces = Pieces(moderator, adPieces.Count > 0 ? adPieces.Count : (int)Math.Ceiling(moderator / TimeSpan.FromMinutes(5)));
        var pairs = Math.Max(adPieces.Count, moderatorPieces.Count);

        // A hot swap keeps the requested song at its position, so breaks start after it when another song follows.
        var firstBreakSong = keepFirstSongFirst && songs.Count > 1 ? 1 : 0;
        var breakSlots = Math.Max(songs.Count - firstBreakSong, 1);
        var breaks = Math.Min(pairs, breakSlots);

        var items = new List<ScheduleItem>();
        var time = start;
        void Add(ScheduleItemKind kind, TimeSpan duration, Song? song = null)
        {
            if (duration <= TimeSpan.Zero)
                return;
            // News, ads and moderator items start without a title; the user can name them in the timeline.
            var title = song is null ? "" : string.IsNullOrWhiteSpace(song.Artist) ? song.Title : $"{song.Artist} - {song.Title}";
            items.Add(new ScheduleItem(kind, title, time, duration, song));
            time += duration;
        }

        var pair = 0;
        void AddBreak(int number)
        {
            // Pairs are dealt out evenly; the last break takes what is left.
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
            // Break n sits before the song at its share of the way through the slot.
            while (nextBreak < breaks && i == firstBreakSong + nextBreak * breakSlots / breaks)
                AddBreak(nextBreak++);
            Add(ScheduleItemKind.Song, songs[i].Duration, songs[i]);
        }
        // No songs at all: the slot is ads and talk only.
        while (nextBreak < breaks)
            AddBreak(nextBreak++);
        return items;
    }

    // Cuts a time into pieces of whole seconds that differ by at most one second, so no piece grows past
    // the limit the count was chosen for. A fraction of a second goes to the last piece.
    private static List<TimeSpan> Pieces(TimeSpan total, int count)
    {
        var pieces = new List<TimeSpan>();
        if (total <= TimeSpan.Zero || count < 1)
            return pieces;
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
