using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Library;
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
        _engine.ItemFailed += (_, e) => ItemFailed?.Invoke(this, $"\"{e.Item.Title}\" übersprungen: {e.Error.Message}");
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

    public float Volume
    {
        get => MusicPlayer.Instance.Volume;
        set => MusicPlayer.Instance.Volume = value;
    }

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
        }
        return plan;
    }

    /// <exception cref="ScheduleException">Library cannot fill the plan.</exception>
    public void StartNow(int? hours)
    {
        CreatePlan(hours, _now());
        _engine.Start(Plan);
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
            ItemFailed?.Invoke(this, $"Der Plan konnte nicht verlängert werden: {ex.Message}");
        }
    }

    public DateTime? WaitingUntil => CurrentItem is null ? _engine.WaitingUntil : null;

    public void Start()
    {
        if (Plan.Count == 0)
            throw new InvalidOperationException("Zuerst einen Plan erstellen.");
        if (Plan[^1].Start <= _now())
            throw new InvalidOperationException("Dieser Plan ist bereits vorbei. Einen neuen erstellen.");
        _engine.Start(Plan);
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
            throw new ScheduleException("Das Element ist bereits am Rand des Plans.");
        return Change(Math.Min(index, target), change: items =>
        {
            var item = items[index];
            items.RemoveAt(index);
            items.Insert(target, item);
        });
    }

    /// <exception cref="ScheduleException">Item on air or over.</exception>
    public IReadOnlyList<ScheduleItem> Remove(int index) => Change(index, change: items => items.RemoveAt(index));

    /// <exception cref="ScheduleException">Nothing on air.</exception>
    public IReadOnlyList<ScheduleItem> EndCurrentNow()
    {
        var index = CurrentIndex;
        if (CurrentItem is null)
            throw new ScheduleException("Es ist nichts auf Sendung.");
        var now = _now();
        // Whole seconds: next start not in the future.
        var end = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind);
        return Change(index, currentAllowed: true, change: items =>
        {
            var item = items[index];
            items[index] = item with { Duration = end > item.Start ? end - item.Start : TimeSpan.Zero };
        });
    }

    /// <exception cref="ScheduleException">Edit not allowed for item, or item over.</exception>
    public IReadOnlyList<ScheduleItem> EditItem(int index, string title, TimeSpan duration) => Change(index, currentAllowed: true, change: items =>
    {
        var item = items[index];
        if (item.Kind == ScheduleItemKind.Song)
            throw new ScheduleException("Songs werden in der Bibliothek bearbeitet.");
        if (duration != item.Duration && item.Kind == ScheduleItemKind.Ad)
            throw new ScheduleException("Nur Nachrichten und Moderation können ihre Länge ändern.");
        if (duration <= TimeSpan.Zero)
            throw new ScheduleException("Die Länge muss größer als null sein.");
        items[index] = item with { Title = title.Trim(), Duration = duration };
    });

    // Ripple re-time from firstChanged; news may leave :00/:30 (intended).
    private IReadOnlyList<ScheduleItem> Change(int firstChanged, Action<List<ScheduleItem>> change, bool currentAllowed = false)
    {
        lock (_planLock)
        {
            if (firstChanged < 0 || firstChanged >= Plan.Count)
                throw new ScheduleException("Ein Element im Plan auswählen.");
            var onAir = IsPlaying && firstChanged == CurrentIndex;
            if (IsPlaying && (firstChanged < CurrentIndex || (onAir && !currentAllowed)))
                throw new ScheduleException(onAir
                    ? "Dieses Element ist auf Sendung. Es kann bearbeitet oder beendet, aber nicht verschoben oder entfernt werden."
                    : "Dieses Element ist bereits vorbei. Ein späteres wählen.");

            List<ScheduleItem> items = [.. Plan];
            var start = items[firstChanged].Start;
            change(items);
            for (var i = firstChanged; i < items.Count; i++)
            {
                items[i] = items[i] with { Start = start };
                start = items[i].End;
            }

            if (IsPlaying && !(onAir ? _engine.ReplaceCurrent(items, firstChanged) : _engine.ReplaceUpcoming(items, firstChanged)))
                throw new ScheduleException("Die Sendung ist inzwischen weitergelaufen. Erneut versuchen.");
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
                throw new ScheduleException("Zuerst einen Plan erstellen.");
            if (index < 0 || index >= Plan.Count)
                throw new ScheduleException("Ein Element im Plan auswählen.");
            if (IsPlaying && index <= CurrentIndex)
                throw new ScheduleException("Dieses Element ist bereits auf Sendung oder vorbei. Ein späteres wählen.");
            var song = _songs.GetById(songId) ?? throw new ScheduleException("Der Song ist nicht mehr in der Bibliothek.");

            var plan = _generator.Replace(Plan, index, song, _songs.GetAll());
            if (IsPlaying && !_engine.ReplaceUpcoming(plan, index))
                throw new ScheduleException("Dieses Element ist inzwischen auf Sendung gegangen. Ein späteres wählen.");
            Plan = plan;
            return plan;
        }
    }
}
