using System.IO;
using NAudio.Wave;

namespace Radiosender.Services;

/// <summary>
/// Plays one audio file at a time on the <see cref="ProgramOutput"/>, the station's audio channel.
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

    private readonly ProgramOutput _output = ProgramOutput.Instance;
    private readonly Lock _lock = new();
    private AudioFileReader? _reader;
    private ISampleProvider? _input; // the reader as it sits in the mixer; null while paused
    private Timer? _positionTimer;
    private float _volume = 1f;

    private MusicPlayer()
    {
        // The mixer reports the end of a track on the audio thread, which must not wait for _lock:
        // Stop holds _lock while it waits for that thread. So the work moves to the thread pool.
        _output.InputEnded += (_, input) => ThreadPool.QueueUserWorkItem(_ => OnInputEnded(input));
        // Releases the file when the app exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    public static MusicPlayer Instance => LazyInstance.Value;

    /// <summary>Raised when a track reaches its end. Not raised by <see cref="Stop"/> or by <see cref="Play"/> replacing a track.</summary>
    public event EventHandler? TrackFinished;

    /// <summary>Raised about every 250 ms while a track is playing, with the current position.</summary>
    public event EventHandler<TimeSpan>? PositionChanged;

    public bool IsPlaying
    {
        get { lock (_lock) return _input is not null; }
    }

    public TimeSpan Position
    {
        get { lock (_lock) return _reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    /// <summary>
    /// Volume of the music from 0 (silent) to 1 (full), before it reaches the mixer, so it applies to listeners and
    /// monitor alike. Kept across tracks; the microphone is not affected.
    /// </summary>
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
                _input = _output.Add(_reader, AudioSource.Music);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or NAudio.MmException))
            {
                Release();
                throw new InvalidDataException($"Audiodatei kann nicht gelesen werden: {filePath}", ex);
            }
            catch
            {
                Release(); // no output device: the file must not stay open
                throw;
            }

            _positionTimer = new Timer(OnPositionTimer, null, PositionInterval, PositionInterval);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_input is null) return;
            _output.Remove(_input);
            _input = null;
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_reader is not null && _input is null) _input = _output.Add(_reader, AudioSource.Music);
        }
    }

    /// <summary>Stops playback and releases the file. Does not raise <see cref="TrackFinished"/>.</summary>
    public void Stop()
    {
        lock (_lock) Release();
    }

    // Caller holds _lock. Clearing _input first keeps Stop and track replacement from raising TrackFinished.
    private void Release()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;

        if (_input is not null)
        {
            // Returns once the audio thread has let go of the reader, so disposing it below is safe.
            _output.Remove(_input);
            _input = null;
        }

        _reader?.Dispose();
        _reader = null;
    }

    private void OnInputEnded(ISampleProvider input)
    {
        lock (_lock)
        {
            // Another input of the channel, or a track that Stop or Play already replaced.
            if (!ReferenceEquals(input, _input)) return;
            Release();
        }

        TrackFinished?.Invoke(this, EventArgs.Empty);
    }

    private void OnPositionTimer(object? state)
    {
        TimeSpan position;
        lock (_lock)
        {
            if (_input is null || _reader is null) return;
            position = _reader.CurrentTime;
        }

        PositionChanged?.Invoke(this, position);
    }
}
