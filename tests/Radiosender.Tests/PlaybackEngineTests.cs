using System.Collections.Concurrent;
using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Scheduling;

namespace Radiosender.Tests;

public class PlaybackEngineTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 14, 0, 0);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>Finishes each track right away unless told to hang or fail.</summary>
    private sealed class FakePlayer : IAudioPlayer
    {
        public ConcurrentQueue<string> Played { get; } = new();
        public int StopCount;
        public string? FailPath;
        public string? HangPath;

        public event EventHandler? TrackFinished;

        public void Play(string filePath)
        {
            if (filePath == FailPath) throw new FileNotFoundException("missing", filePath);
            Played.Enqueue(filePath);
            if (filePath != HangPath)
                TrackFinished?.Invoke(this, EventArgs.Empty);
        }

        public void Stop() => Interlocked.Increment(ref StopCount);
    }

    // Records requested delays and completes them at once. Song timeouts (song length + 10 s) and,
    // with Hold, every delay only end on cancellation.
    private sealed class FakeClock(bool songTimeoutsFire = false)
    {
        private static readonly TimeSpan[] SongTimeouts = [TimeSpan.FromSeconds(210), TimeSpan.FromSeconds(190), TimeSpan.FromSeconds(110)];

        public ConcurrentQueue<TimeSpan> Delays { get; } = new();
        public volatile bool Hold;

        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            Delays.Enqueue(duration);
            var held = Hold || (!songTimeoutsFire && SongTimeouts.Contains(duration));
            return held ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        }
    }

    private static ScheduleItem SongItem(string path, int seconds) =>
        new(ScheduleItemKind.Song, path, Start, TimeSpan.FromSeconds(seconds),
            new Song { Title = path, FilePath = path, Duration = TimeSpan.FromSeconds(seconds) });

    private static ScheduleItem Placeholder(ScheduleItemKind kind, int seconds) =>
        new(kind, kind.ToString(), Start, TimeSpan.FromSeconds(seconds));

    private static readonly IReadOnlyList<ScheduleItem> Plan =
    [
        Placeholder(ScheduleItemKind.News, 300),
        SongItem("a.mp3", 200),
        Placeholder(ScheduleItemKind.Ad, 45),
        Placeholder(ScheduleItemKind.Moderator, 60),
        SongItem("b.mp3", 180),
    ];

    private static (PlaybackEngine Engine, List<int> Started, TaskCompletionSource Finished) Create(FakePlayer player, FakeClock clock)
    {
        var engine = new PlaybackEngine(player, clock.Delay, () => Start);
        var started = new List<int>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ItemStarted += (_, e) => { lock (started) started.Add(e.Index); };
        engine.Finished += (_, _) => finished.TrySetResult();
        return (engine, started, finished);
    }

    [Fact]
    public async Task Start_PlaysAllItemsInOrderWithoutInput()
    {
        var player = new FakePlayer();
        var clock = new FakeClock();
        var (engine, started, finished) = Create(player, clock);

        engine.Start(Plan);
        await finished.Task.WaitAsync(Wait);

        Assert.Equal([0, 1, 2, 3, 4], started);
        Assert.Equal(["a.mp3", "b.mp3"], player.Played);
        // Placeholders are held for their own duration.
        Assert.Contains(TimeSpan.FromSeconds(300), clock.Delays);
        Assert.Contains(TimeSpan.FromSeconds(45), clock.Delays);
        Assert.Contains(TimeSpan.FromSeconds(60), clock.Delays);
        Assert.Equal(-1, engine.CurrentIndex);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task Start_SkipsSongThatCannotBePlayed()
    {
        var player = new FakePlayer { FailPath = "a.mp3" };
        var (engine, started, finished) = Create(player, new FakeClock());
        var failed = new List<ScheduleItem>();
        engine.ItemFailed += (_, e) => failed.Add(e.Item);

        engine.Start(Plan);
        await finished.Task.WaitAsync(Wait);

        Assert.Equal("a.mp3", Assert.Single(failed).Title);
        Assert.Equal(["b.mp3"], player.Played);
        Assert.Equal([0, 1, 2, 3, 4], started);
    }

    [Fact]
    public async Task Start_MovesOnWhenSongNeverFinishes()
    {
        var player = new FakePlayer { HangPath = "a.mp3" };
        var (engine, _, finished) = Create(player, new FakeClock(songTimeoutsFire: true));
        var failed = new List<Exception>();
        engine.ItemFailed += (_, e) => failed.Add(e.Error);

        engine.Start(Plan);
        await finished.Task.WaitAsync(Wait);

        Assert.IsType<TimeoutException>(Assert.Single(failed));
        Assert.Equal(["a.mp3", "b.mp3"], player.Played);
        Assert.True(player.StopCount >= 1);
    }

    [Fact]
    public async Task Stop_EndsPlaybackWithoutFinishedEvent()
    {
        var player = new FakePlayer();
        var clock = new FakeClock { Hold = true };
        var (engine, started, finished) = Create(player, clock);

        engine.Start(Plan);
        while (clock.Delays.IsEmpty) await Task.Delay(10);
        engine.Stop();

        Assert.False(engine.IsRunning);
        Assert.Equal(-1, engine.CurrentIndex);
        Assert.Equal([0], started);
        Assert.False(finished.Task.IsCompleted);
    }

    [Fact]
    public async Task Start_ReplacesARunningSchedule()
    {
        var player = new FakePlayer();
        var clock = new FakeClock { Hold = true };
        var (engine, _, _) = Create(player, clock);

        engine.Start(Plan);
        while (clock.Delays.IsEmpty) await Task.Delay(10);
        clock.Hold = false;
        var finished = new TaskCompletionSource();
        engine.Finished += (_, _) => finished.TrySetResult();
        engine.Start([SongItem("c.mp3", 100)]);
        await finished.Task.WaitAsync(Wait);

        Assert.Equal(["c.mp3"], player.Played);
    }

    [Fact]
    public async Task ReplaceUpcoming_PlaysTheNewItemsButNotOverItemsAlreadyStarted()
    {
        var player = new FakePlayer();
        var news = new TaskCompletionSource();
        var first = true;
        // The first delay is the news; it waits until released. Song timeouts never fire.
        Task Delay(TimeSpan d, CancellationToken t)
        {
            if (first) { first = false; return news.Task.WaitAsync(t); }
            return d > TimeSpan.FromSeconds(180) ? Task.Delay(Timeout.Infinite, t) : Task.CompletedTask;
        }
        var engine = new PlaybackEngine(player, Delay, () => Start);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ItemStarted += (_, _) => started.TrySetResult();
        engine.Finished += (_, _) => finished.TrySetResult();

        engine.Start(Plan);
        await started.Task.WaitAsync(Wait);
        IReadOnlyList<ScheduleItem> swapped = [Plan[0], SongItem("request.mp3", 200), .. Plan.Skip(2)];

        Assert.False(engine.ReplaceUpcoming(swapped, 0));
        Assert.True(engine.ReplaceUpcoming(swapped, 1));
        news.SetResult();
        await finished.Task.WaitAsync(Wait);

        Assert.Equal(["request.mp3", "b.mp3"], player.Played);
    }

    [Fact]
    public async Task Start_SkipsItemsThatShouldHaveStartedEarlierAndWaitsForTheNext()
    {
        var player = new FakePlayer();
        var clock = new FakeClock();
        IReadOnlyList<ScheduleItem> timed =
        [
            Plan[0],
            Plan[1] with { Start = Start.AddSeconds(300) },
            Plan[2] with { Start = Start.AddSeconds(500) },
            Plan[3] with { Start = Start.AddSeconds(545) },
            Plan[4] with { Start = Start.AddSeconds(605) },
        ];
        var engine = new PlaybackEngine(player, clock.Delay, () => Start.AddSeconds(360));
        var started = new List<int>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ItemStarted += (_, e) => started.Add(e.Index);
        engine.Finished += (_, _) => finished.TrySetResult();

        engine.Start(timed);
        await finished.Task.WaitAsync(Wait);

        // News and song a already began at 14:06; the ad waits until its planned 14:08:20.
        Assert.Equal([2, 3, 4], started);
        Assert.Equal(["b.mp3"], player.Played);
        Assert.Equal(TimeSpan.FromSeconds(140), clock.Delays.First());
    }}
