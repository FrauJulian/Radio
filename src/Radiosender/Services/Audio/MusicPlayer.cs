using System.IO;
using NAudio.Wave;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Audio;

// Events: thread pool, never UI thread. WPF: marshal via Dispatcher before touching UI.
public sealed class MusicPlayer : IAudioPlayer
{
    private static readonly Lazy<MusicPlayer> LazyInstance = new(() => new MusicPlayer());

    private readonly ProgramOutput _output = ProgramOutput.Instance;
    private readonly Lock _lock = new();
    private AudioFileReader? _reader;
    private ISampleProvider? _input;

    private MusicPlayer()
    {
        // Audio thread must not wait for _lock (Stop holds it while waiting for that thread) -> thread pool.
        _output.InputEnded += (_, input) => ThreadPool.QueueUserWorkItem(_ => OnInputEnded(input));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    public static MusicPlayer Instance => LazyInstance.Value;

    /// <summary>Not raised by <see cref="Stop"/> or by <see cref="Play"/> replacing a track.</summary>
    public event EventHandler? TrackFinished;

    public bool IsPlaying
    {
        get { lock (_lock) return _input is not null; }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public void Play(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_lock)
        {
            Release();

            if (!File.Exists(filePath))
                throw new FileNotFoundException(Loc.F("Audio_FileNotFound", filePath), filePath);

            try
            {
                _reader = new AudioFileReader(filePath);
                _input = _output.Add(_reader, AudioSource.Music);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or NAudio.MmException))
            {
                Release();
                throw new InvalidDataException(Loc.F("Audio_FileUnreadable", filePath), ex);
            }
            catch
            {
                Release(); // no output device: file must not stay open
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lock) Release();
    }

    // Caller holds _lock. Clearing _input first: Stop/replace never raise TrackFinished.
    private void Release()
    {
        if (_input is not null)
        {
            // Returns after audio thread let go of reader -> safe to dispose below.
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
            if (!ReferenceEquals(input, _input)) return;
            Release();
        }

        TrackFinished?.Invoke(this, EventArgs.Empty);
    }
}
