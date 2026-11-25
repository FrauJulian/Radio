using NAudio.Wave;

namespace Radiosender.Services;

/// <summary>What an input of the mixer is; the levels differ by kind.</summary>
public enum AudioSource
{
    Music,
    Microphone,
}

/// <summary>
/// Mixes every input twice in one pass: once for the listeners (the broadcast) and once for the person at
/// the desk (the monitor). Each input is read only once, so both mixes stay in step.
/// </summary>
public sealed class ProgramMixer
{
    private readonly Lock _lock = new();
    private readonly List<(ISampleProvider Input, AudioSource Kind)> _inputs = [];
    private float[] _scratch = [];

    // Written by the UI thread, read by the audio thread; a level that arrives one block late is fine.
    private volatile float _musicForOthers = 1f;
    private volatile float _microphoneForOthers = 1f;
    private volatile float _musicForMe = 1f;
    private volatile bool _listenToBroadcast = true;

    /// <summary>
    /// Raised when an input has played to its end and was dropped. Comes from the audio thread while the
    /// mixer is busy: handlers must return at once and must not call back into the mixer.
    /// </summary>
    public event EventHandler<ISampleProvider>? InputEnded;

    /// <summary>How loud the music is on the broadcast, from 0 to 1.</summary>
    public float MusicForOthers { get => _musicForOthers; set => _musicForOthers = Level(value); }

    /// <summary>How loud the microphone is on the broadcast, from 0 to 1.</summary>
    public float MicrophoneForOthers { get => _microphoneForOthers; set => _microphoneForOthers = Level(value); }

    /// <summary>How loud the music is on the monitor, from 0 to 1. The monitor never carries the microphone: hearing oneself late is distracting.</summary>
    public float MusicForMe { get => _musicForMe; set => _musicForMe = Level(value); }

    /// <summary>When set, the monitor is an exact copy of the broadcast and <see cref="MusicForMe"/> has no effect.</summary>
    public bool ListenToBroadcast { get => _listenToBroadcast; set => _listenToBroadcast = value; }

    public void Add(ISampleProvider input, AudioSource kind)
    {
        lock (_lock) _inputs.Add((input, kind));
    }

    /// <summary>Returns once the audio thread no longer reads from the input.</summary>
    public void Remove(ISampleProvider input)
    {
        lock (_lock) _inputs.RemoveAll(entry => ReferenceEquals(entry.Input, input));
    }

    /// <summary>Fills both buffers, which must have the same length, with the next block of each mix.</summary>
    public void Mix(Span<float> broadcast, Span<float> monitor)
    {
        broadcast.Clear();
        monitor.Clear();
        if (_scratch.Length < broadcast.Length)
            _scratch = new float[broadcast.Length];
        var block = _scratch.AsSpan(0, broadcast.Length);
        var copyBroadcast = _listenToBroadcast;

        lock (_lock)
        {
            for (var i = _inputs.Count - 1; i >= 0; i--)
            {
                var (input, kind) = _inputs[i];
                var read = input.Read(block);
                var forOthers = kind == AudioSource.Music ? _musicForOthers : _microphoneForOthers;
                var forMe = kind == AudioSource.Music ? _musicForMe : 0f;
                for (var n = 0; n < read; n++)
                {
                    broadcast[n] += block[n] * forOthers;
                    monitor[n] += block[n] * forMe;
                }
                if (read < block.Length)
                {
                    _inputs.RemoveAt(i);
                    InputEnded?.Invoke(this, input);
                }
            }
        }

        // Two loud inputs can add up past full scale, which would distort harshly on the device.
        for (var n = 0; n < broadcast.Length; n++)
        {
            broadcast[n] = Math.Clamp(broadcast[n], -1f, 1f);
            monitor[n] = copyBroadcast ? broadcast[n] : Math.Clamp(monitor[n], -1f, 1f);
        }
    }

    private static float Level(float value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1f);
        return value;
    }
}
