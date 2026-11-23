using Microsoft.Data.Sqlite;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Tests;

public sealed class ScheduleControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));
    private readonly SongRepository _songs;
    private DateTime _now = new(2026, 11, 23, 14, 37, 12);

    public ScheduleControllerTests() => _songs = new SongRepository(Path.Combine(_directory, "test.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class InstantPlayer : IAudioPlayer
    {
        public event EventHandler? TrackFinished;
        public void Play(string filePath) => TrackFinished?.Invoke(this, EventArgs.Empty);
        public void Stop() { }
    }

    private ScheduleController Create(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_songs,
            new ScheduleGenerator(new ScheduleOptions(), new Random(7)),
            new PlaybackEngine(new InstantPlayer(), delay ?? ((_, _) => Task.CompletedTask)),
            () => _now);

    private void AddSongs()
    {
        int[] seconds = [192, 245, 167, 331, 238, 183, 369, 141];
        for (var i = 0; i < seconds.Length; i++)
            _songs.Add(new Song { Title = $"Song {i}", Artist = "Artist", Duration = TimeSpan.FromSeconds(seconds[i]), FilePath = $"{i}.mp3" });
    }

    [Fact]
    public void CreatePlan_StartsAtNextFullHourAndCoversTheHours()
    {
        AddSongs();
        var controller = Create();

        var plan = controller.CreatePlan(2);

        Assert.Equal(new DateTime(2026, 11, 23, 15, 0, 0), plan[0].Start);
        Assert.Equal(new DateTime(2026, 11, 23, 17, 0, 0), plan[^1].End);
        Assert.Same(plan, controller.Plan);
    }

    [Fact]
    public void CreatePlan_ThrowsScheduleExceptionForEmptyLibrary()
    {
        Assert.Throws<ScheduleException>(() => Create().CreatePlan(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void CreatePlan_RejectsHoursOutOfRange(int hours)
    {
        AddSongs();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create().CreatePlan(hours));
    }

    [Fact]
    public void Start_WithoutPlanThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Create().Start());
    }

    [Fact]
    public async Task Start_PlaysThePlanAndRaisesFinished()
    {
        AddSongs();
        var controller = Create();
        controller.CreatePlan(1);
        var started = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ItemStarted += (_, _) => Interlocked.Increment(ref started);
        controller.Finished += (_, _) => finished.TrySetResult();

        controller.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(controller.Plan.Count, started);
        Assert.Null(controller.CurrentItem);
    }

    [Fact]
    public async Task CurrentItemAndElapsed_FollowThePlayingItem()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(1);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ItemStarted += (_, _) => first.TrySetResult();

        controller.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _now = _now.AddMinutes(2);

        Assert.Equal(ScheduleItemKind.News, controller.CurrentItem!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), controller.CurrentElapsed);
        _now = _now.AddHours(1);
        Assert.Equal(controller.CurrentItem.Duration, controller.CurrentElapsed);

        controller.Stop();
        Assert.False(controller.IsPlaying);
        Assert.Null(controller.CurrentItem);
    }

    [Fact]
    public void Swap_ReplacesAnItemBeforeStart()
    {
        AddSongs();
        var controller = Create();
        controller.CreatePlan(1);
        var request = _songs.GetAll()[3];

        var plan = controller.Swap(1, request.Id);

        Assert.Equal(request.Id, plan[1].Song!.Id);
        Assert.Same(plan, controller.Plan);
    }

    [Fact]
    public async Task Swap_WhilePlayingRejectsTheItemOnAirAndAcceptsALaterOne()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(1);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ItemStarted += (_, _) => first.TrySetResult();
        var request = _songs.GetAll()[3];

        controller.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var onAir = Assert.Throws<ScheduleException>(() => controller.Swap(0, request.Id));
        Assert.Contains("already on air", onAir.Message);
        controller.Swap(1, request.Id);
        Assert.Equal(request.Id, controller.Plan[1].Song!.Id);
        controller.Stop();
    }

    [Fact]
    public void Swap_RejectsUnknownSongAndMissingPlan()
    {
        AddSongs();
        var controller = Create();
        Assert.Throws<ScheduleException>(() => controller.Swap(1, _songs.GetAll()[0].Id));

        controller.CreatePlan(1);
        Assert.Throws<ScheduleException>(() => controller.Swap(1, 12345));
    }}
