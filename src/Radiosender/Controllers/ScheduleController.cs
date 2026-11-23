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

    private readonly SongRepository _songs;
    private readonly ScheduleGenerator _generator;
    private readonly PlaybackEngine _engine;
    private readonly Func<DateTime> _now;
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
            ItemStarted?.Invoke(this, e.Index);
        };
        _engine.ItemFailed += (_, e) => ItemFailed?.Invoke(this, $"Skipped \"{e.Item.Title}\": {e.Error.Message}");
        _engine.Finished += (_, _) => Finished?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<int>? ItemStarted;
    public event EventHandler<string>? ItemFailed;
    public event EventHandler? Finished;

    public IReadOnlyList<ScheduleItem> Plan { get; private set; } = [];

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

    /// <summary>Plans <paramref name="hours"/> full hours from the next full hour with the songs in the library.</summary>
    /// <exception cref="ScheduleException">The library cannot fill the plan.</exception>
    public IReadOnlyList<ScheduleItem> CreatePlan(int hours)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hours, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hours, MaxHours);

        var now = _now();
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind).AddHours(1);
        Stop();
        Plan = _generator.Generate(nextHour, hours, _songs.GetAll());
        return Plan;
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
            throw new InvalidOperationException("Create a schedule first.");
        if (Plan[^1].Start <= _now())
            throw new InvalidOperationException("This schedule is already over. Create a new one.");
        _engine.Start(Plan);
    }

    public void Stop() => _engine.Stop();

    /// <summary>All songs in the library, for the song request picker.</summary>
    public IReadOnlyList<Song> LibrarySongs() => _songs.GetAll();

    /// <summary>
    /// Hot swap: plays the song <paramref name="songId"/> in place of the item at <paramref name="index"/>.
    /// The rest of that half hour is rebuilt so the next news still starts on time; works while playing.
    /// </summary>
    /// <exception cref="ScheduleException">The item cannot be replaced or the song does not fit.</exception>
    public IReadOnlyList<ScheduleItem> Swap(int index, long songId)
    {
        if (Plan.Count == 0)
            throw new ScheduleException("Create a schedule first.");
        if (index < 0 || index >= Plan.Count)
            throw new ScheduleException("Select an item in the schedule.");
        if (IsPlaying && index <= CurrentIndex)
            throw new ScheduleException("This item is already on air or over. Choose a later item.");
        var song = _songs.GetById(songId) ?? throw new ScheduleException("The song is no longer in the library.");

        var plan = _generator.Replace(Plan, index, song, _songs.GetAll());
        // The engine may have moved on while the plan was rebuilt.
        if (IsPlaying && !_engine.ReplaceUpcoming(plan, index))
            throw new ScheduleException("This item went on air in the meantime. Choose a later item.");
        Plan = plan;
        return plan;
    }
}
