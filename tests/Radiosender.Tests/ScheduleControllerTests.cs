using Microsoft.Data.Sqlite;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Library;
using Radiosender.Services.Scheduling;

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
            new PlaybackEngine(new InstantPlayer(), delay ?? ((_, _) => Task.CompletedTask), () => _now),
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
    public void CreatePlan_StartsAtTheGivenHour()
    {
        AddSongs();

        var plan = Create().CreatePlan(1, new DateTime(2026, 11, 23, 18, 0, 0));

        Assert.Equal(new DateTime(2026, 11, 23, 18, 0, 0), plan[0].Start);
        Assert.Equal(new DateTime(2026, 11, 23, 19, 0, 0), plan[^1].End);
    }

    [Fact]
    public void CreatePlan_UnlimitedPlansTheLookahead()
    {
        AddSongs();
        var controller = Create();

        var plan = controller.CreatePlan(null);

        Assert.True(controller.IsUnlimited);
        Assert.Equal(new DateTime(2026, 11, 23, 20, 0, 0), plan[^1].End);
    }

    [Fact]
    public async Task Unlimited_AppendsHoursWhilePlaying()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(null); // 15:00 to 20:00
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ItemStarted += (_, _) => first.TrySetResult();

        _now = new DateTime(2026, 11, 23, 16, 30, 0); // the news at 16:30 goes on air
        controller.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var plan = controller.Plan;
        Assert.Equal(new DateTime(2026, 11, 23, 22, 0, 0), plan[^1].End); // 5 h ahead of 16:30, in full hours
        for (var i = 1; i < plan.Count; i++)
            Assert.Equal(plan[i - 1].End, plan[i].Start);
        Assert.Equal(ScheduleItemKind.News, controller.CurrentItem!.Kind);
        controller.Stop();
    }

    [Fact]
    public void PlanFromNow_ShiftsToTheMomentOfTheFirstStart()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        var created = controller.CreatePlan(1, _now);
        Assert.Equal(_now, created[0].Start);
        Assert.NotEqual(ScheduleItemKind.News, created[0].Kind);
        Assert.Equal(new DateTime(2026, 11, 23, 16, 0, 0), created[^1].End);

        _now = _now.AddSeconds(40); // the user waited before pressing Start
        controller.Start();

        Assert.True(controller.IsPlaying);
        Assert.Equal(_now, controller.Plan[0].Start);
        Assert.Equal(created[^1].End.AddSeconds(40), controller.Plan[^1].End);
        Assert.Equal(created.Select(i => i.Duration), controller.Plan.Select(i => i.Duration));
        controller.Stop();
    }

    private static void AssertContiguousFrom(IReadOnlyList<ScheduleItem> plan, DateTime start)
    {
        Assert.Equal(start, plan[0].Start);
        for (var i = 1; i < plan.Count; i++)
            Assert.Equal(plan[i - 1].End, plan[i].Start);
    }

    [Fact]
    public void Move_SwapsItemsAndShiftsTheTimes()
    {
        AddSongs();
        var controller = Create();
        var before = controller.CreatePlan(1);

        var plan = controller.Move(2, -2);

        Assert.Equal(before[2].Song?.Id, plan[0].Song?.Id);
        Assert.Equal(before[2].Kind, plan[0].Kind);
        Assert.Equal(ScheduleItemKind.News, plan[1].Kind);
        Assert.Equal(before.Count, plan.Count);
        AssertContiguousFrom(plan, before[0].Start);
        Assert.Equal(before[^1].End, plan[^1].End);
        Assert.Same(plan, controller.Plan);
    }

    [Fact]
    public void Move_PastTheEdgeThrows()
    {
        AddSongs();
        var controller = Create();
        controller.CreatePlan(1);

        Assert.Throws<ScheduleException>(() => controller.Move(0, -1));
    }

    [Fact]
    public void Insert_AddsAnyKindAndShiftsTheLaterItems()
    {
        AddSongs();
        var controller = Create();
        var plan = controller.CreatePlan(1);
        var song = _songs.GetAll()[0];

        var withAd = controller.Insert(1, ScheduleItemKind.Ad, " Spot ", TimeSpan.FromSeconds(30));
        var withSong = controller.Insert(withAd.Count, ScheduleItemKind.Song, "", TimeSpan.Zero, song.Id);

        Assert.Equal(plan.Count + 2, withSong.Count);
        Assert.Equal(new ScheduleItem(ScheduleItemKind.Ad, "Spot", plan[0].End, TimeSpan.FromSeconds(30)), withSong[1]);
        Assert.Equal(plan[1].Start + TimeSpan.FromSeconds(30), withSong[2].Start);
        Assert.Equal(ScheduleItemKind.Song, withSong[^1].Kind);
        Assert.Equal("Artist - Song 0", withSong[^1].Title);
        Assert.Equal(song.Duration, withSong[^1].Duration);
        Assert.Equal(withSong[^2].End, withSong[^1].Start);
    }

    [Fact]
    public void Insert_RejectsMissingPlanSongAndLength()
    {
        AddSongs();
        var controller = Create();

        Assert.Throws<ScheduleException>(() => controller.Insert(0, ScheduleItemKind.News, "", TimeSpan.FromMinutes(5)));
        controller.CreatePlan(1);
        Assert.Throws<ScheduleException>(() => controller.Insert(0, ScheduleItemKind.Moderator, "", TimeSpan.Zero));
        Assert.Throws<ScheduleException>(() => controller.Insert(0, ScheduleItemKind.Song, "", TimeSpan.Zero, songId: 999));
        Assert.Throws<ScheduleException>(() => controller.Insert(controller.Plan.Count + 1, ScheduleItemKind.Ad, "", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Remove_ClosesTheGap()
    {
        AddSongs();
        var controller = Create();
        var before = controller.CreatePlan(1);

        var plan = controller.Remove(1);

        Assert.Equal(before.Count - 1, plan.Count);
        AssertContiguousFrom(plan, before[0].Start);
        Assert.Equal(before[^1].End - before[1].Duration, plan[^1].End);
    }

    [Fact]
    public void EditItem_SetsTitleAndLengthOfNews()
    {
        AddSongs();
        var controller = Create();
        var before = controller.CreatePlan(1);

        var plan = controller.EditItem(0, " Evening news ", TimeSpan.FromMinutes(3));

        Assert.Equal("Evening news", plan[0].Title);
        Assert.Equal(TimeSpan.FromMinutes(3), plan[0].Duration);
        AssertContiguousFrom(plan, before[0].Start);
        Assert.Equal(before[^1].End - TimeSpan.FromMinutes(2), plan[^1].End);
    }

    [Fact]
    public void EditItem_RejectsSongsAndAdLengths()
    {
        AddSongs();
        var controller = Create();
        var plan = controller.CreatePlan(1);
        var song = plan.ToList().FindIndex(i => i.Kind == ScheduleItemKind.Song);
        var ad = plan.ToList().FindIndex(i => i.Kind == ScheduleItemKind.Ad);

        Assert.Throws<ScheduleException>(() => controller.EditItem(song, "x", plan[song].Duration));
        Assert.Throws<ScheduleException>(() => controller.EditItem(ad, "x", plan[ad].Duration + TimeSpan.FromSeconds(1)));
        Assert.Equal("Spot", controller.EditItem(ad, "Spot", plan[ad].Duration)[ad].Title);
    }

    [Fact]
    public async Task Edits_WhilePlayingRejectTheItemOnAir()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(1);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ItemStarted += (_, _) => first.TrySetResult();
        _now = controller.Plan[0].Start;
        controller.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<ScheduleException>(() => controller.Remove(0));
        Assert.Throws<ScheduleException>(() => controller.Move(1, -1));
        var count = controller.Plan.Count;
        Assert.Equal(count - 1, controller.Remove(1).Count);
        controller.Stop();
    }

    private async Task<(ScheduleController Controller, SemaphoreSlim Started)> OnAirWithTheFirstNewsAsync()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(1);
        var started = new SemaphoreSlim(0);
        controller.ItemStarted += (_, _) => started.Release();
        _now = controller.Plan[0].Start;
        controller.Start();
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)));
        return (controller, started);
    }

    [Fact]
    public async Task EndCurrentNow_EndsTheItemOnAirAndStartsTheNext()
    {
        var (controller, started) = await OnAirWithTheFirstNewsAsync();
        _now = _now.AddMinutes(2);

        var plan = controller.EndCurrentNow();

        Assert.Equal(TimeSpan.FromMinutes(2), plan[0].Duration);
        AssertContiguousFrom(plan, plan[0].Start);
        Assert.Equal(_now, plan[1].Start);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, controller.CurrentIndex);
        controller.Stop();
    }

    [Fact]
    public void EndCurrentNow_WithNothingOnAirThrows()
    {
        AddSongs();
        var controller = Create();
        controller.CreatePlan(1);

        Assert.Throws<ScheduleException>(() => controller.EndCurrentNow());
    }

    [Fact]
    public async Task EditItem_ChangesTheItemOnAirButItCannotBeRemoved()
    {
        var (controller, _) = await OnAirWithTheFirstNewsAsync();
        var start = controller.Plan[0].Start;

        var plan = controller.EditItem(0, "Late news", TimeSpan.FromMinutes(7));

        Assert.Equal("Late news", plan[0].Title);
        Assert.Equal(start.AddMinutes(7), plan[1].Start);
        Assert.Equal(0, controller.CurrentIndex);
        Assert.Equal("Late news", controller.CurrentItem!.Title);
        Assert.Throws<ScheduleException>(() => controller.Remove(0));
        controller.Stop();
    }

    [Fact]
    public async Task EditItem_ShorteningTheItemOnAirBelowItsRunTimeEndsIt()
    {
        var (controller, started) = await OnAirWithTheFirstNewsAsync();
        _now = _now.AddMinutes(3);

        controller.EditItem(0, "", TimeSpan.FromMinutes(1));

        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, controller.CurrentIndex);
        controller.Stop();
    }

    [Fact]
    public void UpcomingSpeech_ReturnsNewsWithinTheLeadTimeWhileOnAir()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(1); // news at 15:00
        var lead = TimeSpan.FromSeconds(30);

        _now = new DateTime(2026, 11, 23, 14, 59, 40);
        Assert.Null(controller.UpcomingSpeech(lead, lead)); // not on air
        controller.Start();
        Assert.Equal(ScheduleItemKind.News, controller.UpcomingSpeech(lead, lead)!.Kind);
        Assert.Null(controller.UpcomingSpeech(null, lead)); // news alert switched off
        _now = new DateTime(2026, 11, 23, 14, 59, 0);
        Assert.Null(controller.UpcomingSpeech(lead, lead));
        Assert.NotNull(controller.UpcomingSpeech(TimeSpan.FromSeconds(60), null)); // longer lead time
        controller.Stop();
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

        _now = controller.Plan[0].Start; // on air at the planned start, so the held delay is the news
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

        _now = controller.Plan[0].Start; // on air at the planned start, so the held delay is the news
        controller.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var onAir = Assert.Throws<ScheduleException>(() => controller.Swap(0, request.Id));
        Assert.Contains("bereits auf Sendung", onAir.Message);
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
    }

    [Fact]
    public async Task Start_WaitsForThePlannedStart()
    {
        AddSongs();
        var hold = new TaskCompletionSource();
        var controller = Create((_, token) => hold.Task.WaitAsync(token));
        controller.CreatePlan(1);

        controller.Start(); // at 14:37:12, the plan starts at 15:00
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (controller.WaitingUntil is null && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Equal(new DateTime(2026, 11, 23, 15, 0, 0), controller.WaitingUntil);
        Assert.True(controller.IsPlaying);
        Assert.Null(controller.CurrentItem);
        controller.Stop();
        Assert.Null(controller.WaitingUntil);
    }

    [Fact]
    public void Start_RejectsAPlanThatIsOver()
    {
        AddSongs();
        var controller = Create();
        controller.CreatePlan(1);
        _now = controller.Plan[^1].Start;

        Assert.Throws<InvalidOperationException>(() => controller.Start());
    }}
