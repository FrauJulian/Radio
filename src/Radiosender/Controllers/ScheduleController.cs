using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Controllers;

/// <summary>
/// Creates the broadcast plan and drives its playback. One instance lives for the whole app
/// (<see cref="App.Schedule"/>), so the plan survives page navigation.
/// </summary>
/// <remarks>Events come from the playback engine's thread pool; views marshal with <c>Dispatcher.BeginInvoke</c>.</remarks>
public class ScheduleController
{
    public const int MaxHours = 24;

    /// <summary>How far ahead an unlimited plan always reaches.</summary>
    public const int UnlimitedLookaheadHours = 5;

    private readonly SongRepository _songs;
    private readonly ScheduleGenerator _generator;
    private readonly PlaybackEngine _engine;
    private readonly Func<DateTime> _now;
    // Guards Plan against a song request (UI thread) racing the hourly extension (engine thread).
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

    /// <summary><c>true</c> for a plan without an end: it grows by an hour whenever less than
    /// <see cref="UnlimitedLookaheadHours"/> hours are left.</summary>
    public bool IsUnlimited { get; private set; }

    public bool IsPlaying => _engine.IsRunning;

    public int CurrentIndex => _engine.CurrentIndex;

    /// <summary>The item on air, or <c>null</c> when nothing plays.</summary>
    public ScheduleItem? CurrentItem => CurrentIndex is var i && i >= 0 && i < _engine.Items.Count ? _engine.Items[i] : null;

    /// <summary>Time since the current item started, capped at its duration.</summary>
    public TimeSpan CurrentElapsed => CurrentItem is { } item
        ? TimeSpan.FromTicks(Math.Clamp((_now() - _itemStartedAt).Ticks, 0, item.Duration.Ticks))
        : TimeSpan.Zero;

    /// <summary>Playback volume from 0 to 1.</summary>
    public float Volume
    {
        get => MusicPlayer.Instance.Volume;
        set => MusicPlayer.Instance.Volume = value;
    }

    /// <summary>
    /// Plans <paramref name="hours"/> full hours with the songs in the library; <c>null</c> plans without an end.
    /// The plan begins at <paramref name="start"/>, or at the next full hour when that is <c>null</c>.
    /// </summary>
    /// <exception cref="ScheduleException">The library cannot fill the plan.</exception>
    public IReadOnlyList<ScheduleItem> CreatePlan(int? hours, DateTime? start = null)
    {
        if (hours is { } h)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(h, 1, nameof(hours));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(h, MaxHours, nameof(hours));
        }

        var now = _now();
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind).AddHours(1);
        // Whole seconds, so the timeline and the item ends stay on the second.
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

    /// <summary>Plans from this moment and goes on air at once; music leads up to the first news at :00 or :30.</summary>
    /// <exception cref="ScheduleException">The library cannot fill the plan.</exception>
    public void StartNow(int? hours)
    {
        CreatePlan(hours, _now());
        _engine.Start(Plan);
    }

    // Runs on the engine thread whenever an item starts.
    // shortcut: played items stay in the plan (about 20 per hour); trim them if broadcasts run for weeks.
    private void ExtendIfUnlimited()
    {
        if (!IsUnlimited)
            return;
        try
        {
            lock (_planLock)
            {
                var plan = Plan;
                if (plan.Count == 0) // the user removed every item
                    return;
                var until = _now().AddHours(UnlimitedLookaheadHours);
                while (plan[^1].End < until)
                    plan = [.. plan, .. _generator.Generate(plan[^1].End, 1, _songs.GetAll())];
                // Only appends, so the engine keeps everything up to the old end.
                if (!ReferenceEquals(plan, Plan) && _engine.ReplaceUpcoming(plan, Plan.Count))
                    Plan = plan;
            }
        }
        catch (ScheduleException ex)
        {
            ItemFailed?.Invoke(this, $"Der Plan konnte nicht verlängert werden: {ex.Message}");
        }
    }

    /// <summary>Planned start of the next item while the broadcast waits for it, otherwise <c>null</c>.</summary>
    public DateTime? WaitingUntil => CurrentItem is null ? _engine.WaitingUntil : null;

    /// <summary>
    /// Goes on air with the plan on the real clock: waits for the planned start, or joins at the next item
    /// when the plan has already begun.
    /// </summary>
    public void Start()
    {
        if (Plan.Count == 0)
            throw new InvalidOperationException("Zuerst einen Plan erstellen.");
        if (Plan[^1].Start <= _now())
            throw new InvalidOperationException("Dieser Plan ist bereits vorbei. Einen neuen erstellen.");
        _engine.Start(Plan);
    }

    public void Stop() => _engine.Stop();

    /// <summary>
    /// The next news or moderator item that is due for a reminder while the broadcast runs: it starts within
    /// the lead time of its kind. <c>null</c> as lead time switches that kind off. Returns <c>null</c> when
    /// nothing is due.
    /// </summary>
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

    /// <summary>Moves the item at <paramref name="index"/> by <paramref name="offset"/> positions; later items shift in time.</summary>
    /// <exception cref="ScheduleException">An affected item is already on air or over, or the target is outside the plan.</exception>
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

    /// <summary>Removes the item at <paramref name="index"/>; later items move up in time.</summary>
    /// <exception cref="ScheduleException">The item is already on air or over.</exception>
    public IReadOnlyList<ScheduleItem> Remove(int index) => Change(index, change: items => items.RemoveAt(index));

    /// <summary>Ends the item on air now: its length becomes the time it has run, and the next item starts.</summary>
    /// <exception cref="ScheduleException">Nothing is on air.</exception>
    public IReadOnlyList<ScheduleItem> EndCurrentNow()
    {
        var index = CurrentIndex;
        if (CurrentItem is null)
            throw new ScheduleException("Es ist nichts auf Sendung.");
        var now = _now();
        // Whole seconds, like every other time in the plan; the next start is then not in the future.
        var end = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind);
        return Change(index, currentAllowed: true, change: items =>
        {
            var item = items[index];
            items[index] = item with { Duration = end > item.Start ? end - item.Start : TimeSpan.Zero };
        });
    }

    /// <summary>
    /// Sets the title of a news, ad or moderator item, and the length of a news or moderator item;
    /// later items shift in time. Also works for the item on air: its new length counts from its start.
    /// </summary>
    /// <exception cref="ScheduleException">The item cannot be edited that way, or is already over.</exception>
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

    // Manual edits ripple: from the first changed position on, every item starts when the one before ends.
    // News can therefore leave :00 and :30; that is the user's call.
    // currentAllowed: the item on air may change too; the engine is then told to re-time it.
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

            var items = Plan.ToList();
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

    /// <summary>All songs in the library, for the song request picker.</summary>
    public IReadOnlyList<Song> LibrarySongs() => _songs.GetAll();

    /// <summary>
    /// Hot swap: plays the song <paramref name="songId"/> in place of the item at <paramref name="index"/>.
    /// The rest of that half hour is rebuilt so the next news still starts on time; works while playing.
    /// </summary>
    /// <exception cref="ScheduleException">The item cannot be replaced or the song does not fit.</exception>
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
            // The engine may have moved on while the plan was rebuilt.
            if (IsPlaying && !_engine.ReplaceUpcoming(plan, index))
                throw new ScheduleException("Dieses Element ist inzwischen auf Sendung gegangen. Ein späteres wählen.");
            Plan = plan;
            return plan;
        }
    }
}
