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

    /// <summary>Plays the plan from its first item; the planned times are the on-air clock, playback starts right away.</summary>
    public void Start()
    {
        if (Plan.Count == 0)
            throw new InvalidOperationException("Create a schedule first.");
        _engine.Start(Plan);
    }

    public void Stop() => _engine.Stop();
}
