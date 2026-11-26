using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Scheduling;

// Songs never cut. Events: thread pool; WPF use BeginInvoke, not Invoke (Stop waits -> deadlock).
public sealed class PlaybackEngine(IAudioPlayer player, Func<TimeSpan, CancellationToken, Task> delay, Func<DateTime> now)
{
    // Stuck-song grace, e.g. device error that never raises TrackFinished.
    private static readonly TimeSpan SongGrace = TimeSpan.FromSeconds(10);

    // Item started at most this long ago still plays on start (click right after 15:00).
    private static readonly TimeSpan LateStart = TimeSpan.FromSeconds(5);

    private readonly IAudioPlayer _player = player;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay;
    private readonly Func<DateTime> _now = now;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;

    // Set by ReplaceCurrent to wake the on-air wait; new one per wait.
    private TaskCompletionSource? _currentChanged;

    public PlaybackEngine() : this(MusicPlayer.Instance, Task.Delay, () => DateTime.Now)
    {
    }

    public event EventHandler<(int Index, ScheduleItem Item)>? ItemStarted;

    public event EventHandler<(ScheduleItem Item, Exception Error)>? ItemFailed;

    /// <summary>Not raised by <see cref="Stop"/>.</summary>
    public event EventHandler? Finished;

    public IReadOnlyList<ScheduleItem> Items { get; private set; } = [];

    public int CurrentIndex { get; private set; } = -1;

    public bool IsRunning => !_run.IsCompleted;

    public DateTime? WaitingUntil { get; private set; }

    public void Start(IReadOnlyList<ScheduleItem> items)
    {
        Stop();
        lock (_lock)
        {
            Items = items;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _run = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Items before <paramref name="fromIndex"/> must be unchanged. False if that item already started.</summary>
    public bool ReplaceUpcoming(IReadOnlyList<ScheduleItem> items, int fromIndex)
    {
        lock (_lock)
        {
            if (IsRunning && CurrentIndex >= fromIndex)
                return false;
            Items = items;
            return true;
        }
    }

    /// <summary>On-air item changed: hold item gets new length from its start, song ends at once. False if not on air.</summary>
    public bool ReplaceCurrent(IReadOnlyList<ScheduleItem> items, int index)
    {
        lock (_lock)
        {
            if (!IsRunning || CurrentIndex != index || index >= items.Count)
                return false;
            Items = items;
            _currentChanged?.TrySetResult();
            return true;
        }
    }

    public void Stop()
    {
        Task run;
        lock (_lock)
        {
            _cts?.Cancel();
            run = _run;
        }

        try
        {
            run.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }

        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var skipBefore = _now() - LateStart;
            for (var i = 0; ; i++)
            {
                token.ThrowIfCancellationRequested();
                ScheduleItem item;
                lock (_lock)
                {
                    if (i >= Items.Count) break;
                    item = Items[i];
                }
                if (item.Start < skipBefore)
                    continue;

                var wait = item.Start - _now();
                if (wait > TimeSpan.Zero)
                {
                    WaitingUntil = item.Start;
                    try { await _delay(wait, token); }
                    finally { WaitingUntil = null; }
                }

                // Lock: ReplaceUpcoming must not swap the item about to start.
                lock (_lock)
                {
                    if (i >= Items.Count) break;
                    item = Items[i];
                    CurrentIndex = i;
                }
                ItemStarted?.Invoke(this, (i, item));

                if (item.Kind == ScheduleItemKind.Song && item.Song is not null)
                    await PlaySongAsync(item, token);
                else
                    await HoldAsync(i, token);
            }

            CurrentIndex = -1;
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            CurrentIndex = -1;
        }
    }

    private async Task HoldAsync(int index, CancellationToken token)
    {
        var startedAt = _now();
        while (true)
        {
            TaskCompletionSource changed;
            TimeSpan remaining;
            lock (_lock)
            {
                changed = _currentChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                remaining = Items[index].Duration - (_now() - startedAt);
            }
            if (remaining <= TimeSpan.Zero)
                return;

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            var elapsed = _delay(remaining, wait.Token);
            var done = await Task.WhenAny(elapsed, changed.Task);
            await wait.CancelAsync();
            token.ThrowIfCancellationRequested();
            if (done == elapsed)
                return;
        }
    }

    private async Task PlaySongAsync(ScheduleItem item, CancellationToken token)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(object? sender, EventArgs e) => finished.TrySetResult();

        _player.TrackFinished += OnFinished;
        try
        {
            try
            {
                _player.Play(item.Song!.FilePath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ItemFailed?.Invoke(this, (item, ex));
                return;
            }

            TaskCompletionSource changed;
            lock (_lock)
                changed = _currentChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stuck = _delay(item.Duration + SongGrace, timeout.Token);
            var done = await Task.WhenAny(finished.Task, stuck, changed.Task);
            await timeout.CancelAsync();
            token.ThrowIfCancellationRequested();
            if (done == stuck)
                ItemFailed?.Invoke(this, (item, new TimeoutException(Loc.F("Scheduling_SongTimeout", item.Title))));
        }
        finally
        {
            _player.TrackFinished -= OnFinished;
            // Cancel, timeout, or user ended song (changed): stop player.
            if (!finished.Task.IsCompleted) _player.Stop();
        }
    }
}
