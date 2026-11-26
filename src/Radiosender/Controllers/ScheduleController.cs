using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Library;
using Radiosender.Services.Localization;
using Radiosender.Services.Scheduling;

namespace Radiosender.Controllers;

// Events fire on engine thread; views marshal with Dispatcher.BeginInvoke.
public class ScheduleController
{
    private const int MaxHours = 24;

    private const int UnlimitedLookaheadHours = 5;

    private readonly SongRepository _songs;
    private readonly ScheduleGenerator _generator;
    private readonly PlaybackEngine _engine;
    private readonly Func<DateTime> _now;
    // Plan edits (UI thread) vs. hourly extension (engine thread).
    private readonly Lock _planLock = new();
    private bool _fromNow; // plan starts at its creation moment, not at a set time
    private DateTime _itemStartedAt;

    public ScheduleController(SongRepository songs, ScheduleGenerator generator, PlaybackEngine engine, Func<DateTime> now)
    {
        _songs = songs;
        _generator = generator;
        _engine = engine;
        _now = now;
        _engine.ItemStarted += (_, e) =>
        {
            _itemStartedAt = _now();
            ExtendIfUnlimited();
            ItemStarted?.Invoke(this, e.Index);
        };
        _engine.ItemFailed += (_, e) => ItemFailed?.Invoke(this, Loc.F("Schedule_ItemFailedText", e.Item.Title, e.Error.Message));
        _engine.Finished += (_, _) => Finished?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<int>? ItemStarted;
    public event EventHandler<string>? ItemFailed;
    public event EventHandler? Finished;

    public IReadOnlyList<ScheduleItem> Plan { get; private set; } = [];

    public bool IsUnlimited { get; private set; }

    public bool IsPlaying => _engine.IsRunning;

    public int CurrentIndex => _engine.CurrentIndex;

    public ScheduleItem? CurrentItem => CurrentIndex is var i && i >= 0 && i < _engine.Items.Count ? _engine.Items[i] : null;

    public TimeSpan CurrentElapsed => CurrentItem is { } item
        ? TimeSpan.FromTicks(Math.Clamp((_now() - _itemStartedAt).Ticks, 0, item.Duration.Ticks))
        : TimeSpan.Zero;

    // hours null = unlimited; start null = next full hour.
    /// <exception cref="ScheduleException">Library cannot fill the plan.</exception>
    public IReadOnlyList<ScheduleItem> CreatePlan(int? hours, DateTime? start = null)
    {
        if (hours is { } h)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(h, 1, nameof(hours));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(h, MaxHours, nameof(hours));
        }

        var now = _now();
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind).AddHours(1);
        var from = start is { } s ? new DateTime(s.Ticks - s.Ticks % TimeSpan.TicksPerSecond, s.Kind) : nextHour;
        Stop();
        var plan = _generator.Generate(from, hours ?? UnlimitedLookaheadHours, _songs.GetAll());
        lock (_planLock)
        {
            Plan = plan;
            IsUnlimited = hours is null;
            _fromNow = from <= now;
        }
        return plan;
    }

    // Engine thread. shortcut: played items stay in Plan (~20/h), trim if broadcasts run for weeks.
    private void ExtendIfUnlimited()
    {
        if (!IsUnlimited)
            return;
        try
        {
            lock (_planLock)
            {
                var plan = Plan;
                if (plan.Count == 0)
                    return;
                var until = _now().AddHours(UnlimitedLookaheadHours);
                while (plan[^1].End < until)
                    plan = [.. plan, .. _generator.Generate(plan[^1].End, 1, _songs.GetAll())];
                // Append only: engine keeps items up to old end.
                if (!ReferenceEquals(plan, Plan) && _engine.ReplaceUpcoming(plan, Plan.Count))
                    Plan = plan;
            }
        }
        catch (ScheduleException ex)
        {
            ItemFailed?.Invoke(this, Loc.F("Schedule_ExtendFailed", ex.Message));
        }
    }

    public DateTime? WaitingUntil => CurrentItem is null ? _engine.WaitingUntil : null;

    public void Start()
    {
        lock (_planLock)
        {
            if (Plan.Count == 0)
                throw new InvalidOperationException(Loc.T("Schedule_CreateFirst"));
            var now = _now();
            // "Jetzt" plan, first start: shift to now, else the engine skips what began while the user waited.
            // shortcut: news drift by that wait; regenerate if users wait minutes between create and start.
            if (_fromNow && Plan[0].Start < now)
            {
                var offset = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind) - Plan[0].Start;
                Plan = [.. Plan.Select(item => item with { Start = item.Start + offset })];
            }
            _fromNow = false;
            if (Plan[^1].Start <= now)
                throw new InvalidOperationException(Loc.T("Schedule_PlanOver"));
            _engine.Start(Plan);
        }
    }

    public void Stop() => _engine.Stop();

    // Next news/moderator item within its lead time; null lead = kind off.
    public ScheduleItem? UpcomingSpeech(TimeSpan? newsLead, TimeSpan? moderatorLead)
    {
        if (!IsPlaying)
            return null;
        var now = _now();
        var plan = Plan;
        var longest = TimeSpan.FromTicks(Math.Max((newsLead ?? TimeSpan.Zero).Ticks, (moderatorLead ?? TimeSpan.Zero).Ticks));
        for (var i = Math.Max(CurrentIndex + 1, 0); i < plan.Count; i++)
        {
            var wait = plan[i].Start - now;
            if (wait > longest)
                return null;
            var lead = plan[i].Kind switch
            {
                ScheduleItemKind.News => newsLead,
                ScheduleItemKind.Moderator => moderatorLead,
                _ => null,
            };
            if (wait > TimeSpan.Zero && wait <= lead)
                return plan[i];
        }
        return null;
    }

    /// <exception cref="ScheduleException">Item on air/over, or target outside plan.</exception>
    public IReadOnlyList<ScheduleItem> Move(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= Plan.Count)
            throw new ScheduleException(Loc.T("Schedule_AtEdge"));
        return Change(Math.Min(index, target), change: items =>
        {
            var item = items[index];
            items.RemoveAt(index);
            items.Insert(target, item);
        });
    }

    // index == Plan.Count appends. Song: songId required, its length is used.
    /// <exception cref="ScheduleException">No plan, position on air or over, song missing, or length not positive.</exception>
    public IReadOnlyList<ScheduleItem> Insert(int index, ScheduleItemKind kind, string title, TimeSpan duration, long? songId = null)
    {
        Song? song = null;
        if (kind == ScheduleItemKind.Song)
        {
            song = (songId is { } id ? _songs.GetById(id) : null) ?? throw new ScheduleException(Loc.T("Schedule_SongGone"));
            title = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : $"{song.Artist} - {song.Title}";
            duration = song.Duration;
        }
        if (duration <= TimeSpan.Zero)
            throw new ScheduleException(Loc.T("Schedule_LengthPositive"));
        var item = new ScheduleItem(kind, title.Trim(), default, duration, song);
        return Change(index, change: items => items.Insert(index, item), appendAllowed: true);
    }

    /// <exception cref="ScheduleException">Item on air or over.</exception>
    public IReadOnlyList<ScheduleItem> Remove(int index) => Change(index, change: items => items.RemoveAt(index));

    /// <exception cref="ScheduleException">Edit not allowed for item, or item over.</exception>
    public IReadOnlyList<ScheduleItem> EditItem(int index, string title, TimeSpan duration) => Change(index, currentAllowed: true, change: items =>
    {
        var item = items[index];
        if (item.Kind == ScheduleItemKind.Song)
            throw new ScheduleException(Loc.T("Schedule_SongsEditInLibrary"));
        if (duration != item.Duration && item.Kind == ScheduleItemKind.Ad)
            throw new ScheduleException(Loc.T("Schedule_OnlyNewsModeratorLength"));
        if (duration <= TimeSpan.Zero)
            throw new ScheduleException(Loc.T("Schedule_LengthPositive"));
        items[index] = item with { Title = title.Trim(), Duration = duration };
    });

    // Ripple re-time from firstChanged; news may leave :00/:30 (intended).
    private IReadOnlyList<ScheduleItem> Change(int firstChanged, Action<List<ScheduleItem>> change, bool currentAllowed = false, bool appendAllowed = false)
    {
        lock (_planLock)
        {
            if (Plan.Count == 0)
                throw new ScheduleException(Loc.T("Schedule_CreateFirst"));
            if (firstChanged < 0 || firstChanged > Plan.Count || (firstChanged == Plan.Count && !appendAllowed))
                throw new ScheduleException(Loc.T("Schedule_SelectItem"));
            var onAir = IsPlaying && firstChanged == CurrentIndex;
            if (IsPlaying && (firstChanged < CurrentIndex || (onAir && !currentAllowed)))
                throw new ScheduleException(Loc.T(onAir ? "Schedule_OnAirLocked" : "Schedule_ItemOver"));

            List<ScheduleItem> items = [.. Plan];
            var start = firstChanged < items.Count ? items[firstChanged].Start : items[^1].End;
            change(items);
            for (var i = firstChanged; i < items.Count; i++)
            {
                items[i] = items[i] with { Start = start };
                start = items[i].End;
            }

            if (IsPlaying && !(onAir ? _engine.ReplaceCurrent(items, firstChanged) : _engine.ReplaceUpcoming(items, firstChanged)))
                throw new ScheduleException(Loc.T("Schedule_RunMovedOn"));
            Plan = items;
            return items;
        }
    }

    public IReadOnlyList<Song> LibrarySongs() => _songs.GetAll();

    // Hot swap: rest of half hour rebuilt so next news stays on time.
    /// <exception cref="ScheduleException">Item not replaceable or song does not fit.</exception>
    public IReadOnlyList<ScheduleItem> Swap(int index, long songId)
    {
        lock (_planLock)
        {
            if (Plan.Count == 0)
                throw new ScheduleException(Loc.T("Schedule_CreateFirst"));
            if (index < 0 || index >= Plan.Count)
                throw new ScheduleException(Loc.T("Schedule_SelectItem"));
            if (IsPlaying && index <= CurrentIndex)
                throw new ScheduleException(Loc.T("Schedule_SwapOnAirOrOver"));
            var song = _songs.GetById(songId) ?? throw new ScheduleException(Loc.T("Schedule_SongGone"));

            var plan = _generator.Replace(Plan, index, song, _songs.GetAll());
            if (IsPlaying && !_engine.ReplaceUpcoming(plan, index))
                throw new ScheduleException(Loc.T("Schedule_SwapWentOnAir"));
            Plan = plan;
            return plan;
        }
    }
}
