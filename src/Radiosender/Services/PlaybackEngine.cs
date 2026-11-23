using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>
/// Runs a generated schedule from start to end: songs play through the audio player, news, ads and
/// moderator placeholders are held for their duration.
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

    private readonly IAudioPlayer _player;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;

    public PlaybackEngine() : this(MusicPlayer.Instance, Task.Delay)
    {
    }

    public PlaybackEngine(IAudioPlayer player, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _player = player;
        _delay = delay;
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

    /// <summary>Stops any running schedule and starts <paramref name="items"/> from the first item.</summary>
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
            for (var i = 0; ; i++)
            {
                token.ThrowIfCancellationRequested();
                ScheduleItem item;
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
                    await _delay(item.Duration, token);
            }

            CurrentIndex = -1;
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            CurrentIndex = -1;
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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stuck = _delay(item.Duration + SongGrace, timeout.Token);
            var done = await Task.WhenAny(finished.Task, stuck);
            timeout.Cancel();
            token.ThrowIfCancellationRequested();
            if (done == stuck)
                ItemFailed?.Invoke(this, (item, new TimeoutException($"Song did not finish in time: {item.Title}")));
        }
        finally
        {
            _player.TrackFinished -= OnFinished;
            // On cancel, timeout or a failed start the song has not ended by itself.
            if (!finished.Task.IsCompleted) _player.Stop();
        }
    }
}
