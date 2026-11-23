using System.IO;
using NAudio.Wave;

namespace Radiosender.Services;

/// <summary>
/// Plays one audio file at a time through the default output device.
/// </summary>
/// <remarks>
/// <see cref="TrackFinished"/> and <see cref="PositionChanged"/> are raised on a thread-pool thread,
/// never on the WPF UI thread. WPF callers must marshal with <c>Dispatcher.Invoke</c> or
/// <c>Dispatcher.BeginInvoke</c> before touching UI elements.
/// </remarks>
public sealed class MusicPlayer : IAudioPlayer
{
    private static readonly Lazy<MusicPlayer> LazyInstance = new(() => new MusicPlayer());

    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private WaveOut? _output;
    private AudioFileReader? _reader;
    private Timer? _positionTimer;
    private float _volume = 1f;

    private MusicPlayer()
    {
        // Releases the audio device when the app exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    public static MusicPlayer Instance => LazyInstance.Value;

    /// <summary>Raised when a track reaches its end. Not raised by <see cref="Stop"/> or by <see cref="Play"/> replacing a track.</summary>
    public event EventHandler? TrackFinished;

    /// <summary>Raised about every 250 ms while a track is playing, with the current position.</summary>
    public event EventHandler<TimeSpan>? PositionChanged;

    public bool IsPlaying
    {
        get { lock (_lock) return _output?.PlaybackState == PlaybackState.Playing; }
    }

    public TimeSpan Position
    {
        get { lock (_lock) return _reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    /// <summary>Playback volume from 0 (silent) to 1 (full). Kept across tracks.</summary>
    public float Volume
    {
        get { lock (_lock) return _volume; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1f);
            lock (_lock)
            {
                _volume = value;
                if (_reader is not null) _reader.Volume = value;
            }
        }
    }

    /// <summary>Stops the current track and starts playing <paramref name="filePath"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable audio file.</exception>
    public void Play(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_lock)
        {
            Release();

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Audiodatei nicht gefunden: {filePath}", filePath);

            try
            {
                _reader = new AudioFileReader(filePath) { Volume = _volume };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException($"Audiodatei kann nicht gelesen werden: {filePath}", ex);
            }

            try
            {
                _output = new WaveOut();
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_reader);
                _output.Play();
                _positionTimer = new Timer(OnPositionTimer, null, PositionInterval, PositionInterval);
            }
            catch
            {
                Release();
                throw;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_output?.PlaybackState == PlaybackState.Playing) _output.Pause();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_output?.PlaybackState == PlaybackState.Paused) _output.Play();
        }
    }

    /// <summary>Stops playback and releases the file and audio device. Does not raise <see cref="TrackFinished"/>.</summary>
    public void Stop()
    {
        lock (_lock) Release();
    }

    // Caller holds _lock. Unsubscribing first keeps Stop and track replacement from raising TrackFinished.
    private void Release()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;

        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            // A stale event from an output that Stop or Play already released.
            if (!ReferenceEquals(sender, _output)) return;
            Release();
        }

        // An exception means a device error, not a natural end of the track.
        if (e.Exception is null)
            ThreadPool.QueueUserWorkItem(_ => TrackFinished?.Invoke(this, EventArgs.Empty));
    }

    private void OnPositionTimer(object? state)
    {
        TimeSpan position;
        lock (_lock)
        {
            if (_output?.PlaybackState != PlaybackState.Playing || _reader is null) return;
            position = _reader.CurrentTime;
        }

        PositionChanged?.Invoke(this, position);
    }
}
