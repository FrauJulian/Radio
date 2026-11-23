using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>
/// Runs a generated schedule on the real clock: each item starts at its planned time, or right after the
/// previous item if that one ran late. Songs play through the audio player, news, ads and moderator
/// placeholders are held for their duration. Songs are never cut.
/// </summary>
/// <remarks>
/// Events are raised on the engine's thread-pool thread. WPF callers must marshal with
/// <c>Dispatcher.BeginInvoke</c>, not <c>Dispatcher.Invoke</c>: <see cref="Stop"/> waits for that thread,
/// so a handler that blocks on the UI thread while the UI calls Stop would deadlock.
/// </remarks>
public sealed class PlaybackEngine
{
    // Extra time before a song counts as stuck, e.g. after an audio device error that never raises TrackFinished.
    private static readonly TimeSpan SongGrace = TimeSpan.FromSeconds(10);

    // An item that started at most this long ago still plays when the broadcast starts, e.g. a click right after 15:00.
    private static readonly TimeSpan LateStart = TimeSpan.FromSeconds(5);

    private readonly IAudioPlayer _player;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _now;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;

    // Completed by ReplaceCurrent to wake the wait of the item on air; a new one per wait.
    private TaskCompletionSource? _currentChanged;

    public PlaybackEngine() : this(MusicPlayer.Instance, Task.Delay, () => DateTime.Now)
    {
    }

    public PlaybackEngine(IAudioPlayer player, Func<TimeSpan, CancellationToken, Task> delay, Func<DateTime> now)
    {
        _player = player;
        _delay = delay;
        _now = now;
    }

    /// <summary>Raised when an item starts, with its index in <see cref="Items"/>.</summary>
    public event EventHandler<(int Index, ScheduleItem Item)>? ItemStarted;

    /// <summary>Raised when a song cannot be played; the engine then moves on to the next item.</summary>
    public event EventHandler<(ScheduleItem Item, Exception Error)>? ItemFailed;

    /// <summary>Raised after the last item has ended. Not raised by <see cref="Stop"/>.</summary>
    public event EventHandler? Finished;

    public IReadOnlyList<ScheduleItem> Items { get; private set; } = [];

    /// <summary>Index of the item that is playing, or -1 when the engine is not running.</summary>
    public int CurrentIndex { get; private set; } = -1;

    public bool IsRunning => !_run.IsCompleted;

    /// <summary>Planned start the engine waits for, or <c>null</c> when it is not waiting.</summary>
    public DateTime? WaitingUntil { get; private set; }

    /// <summary>
    /// Stops any running schedule and starts <paramref name="items"/> on the clock. Items that should have
    /// started earlier are skipped; before the first planned start the engine waits.
    /// </summary>
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

    /// <summary>
    /// Hot swap: switches to <paramref name="items"/> while playing. Items before <paramref name="fromIndex"/>
    /// must be unchanged. Returns <c>false</c> if the item at <paramref name="fromIndex"/> has already started.
    /// </summary>
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

    /// <summary>
    /// Switches to <paramref name="items"/> while the item at <paramref name="index"/> is on air and that item
    /// itself changed: a news, ad or moderator item then runs for its new length, counted from when it started,
    /// and a song ends at once. Items before <paramref name="index"/> must be unchanged. Returns <c>false</c>
    /// if that item is not the one on air.
    /// </summary>
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

    /// <summary>Stops playback. Waits until the running schedule has ended.</summary>
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

                // Early items wait for their slot, so the news hits :00 and :30; late items start right away.
                var wait = item.Start - _now();
                if (wait > TimeSpan.Zero)
                {
                    WaitingUntil = item.Start;
                    try { await _delay(wait, token); }
                    finally { WaitingUntil = null; }
                }

                // Under the lock so ReplaceUpcoming cannot swap the item that is about to start.
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

    // Keeps a news, ad or moderator item on air for its length. ReplaceCurrent can change that length
    // meanwhile; the wait then starts over with what is left.
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
            var delay = _delay(remaining, wait.Token);
            var done = await Task.WhenAny(delay, changed.Task);
            wait.Cancel();
            token.ThrowIfCancellationRequested();
            if (done == delay)
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
            // changed: the user ended the song early; the finally block stops the player.
            var done = await Task.WhenAny(finished.Task, stuck, changed.Task);
            timeout.Cancel();
            token.ThrowIfCancellationRequested();
            if (done == stuck)
                ItemFailed?.Invoke(this, (item, new TimeoutException($"Der Song wurde nicht rechtzeitig fertig: {item.Title}")));
        }
        finally
        {
            _player.TrackFinished -= OnFinished;
            // On cancel, timeout or a failed start the song has not ended by itself.
            if (!finished.Task.IsCompleted) _player.Stop();
        }
    }
}
