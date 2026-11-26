using NAudio.Wave;

namespace Radiosender.Services.Audio;

public sealed class ProgramMixer
{
    private readonly Lock _lock = new();
    private readonly List<(ISampleProvider Input, AudioSource Kind)> _inputs = [];
    private float[] _scratch = [];

    // UI thread writes, audio thread reads; one block late is fine.
    private volatile float _musicForOthers = 1f;
    private volatile float _microphoneForOthers = 1f;
    private volatile float _musicForMe = 1f;
    private volatile bool _listenToBroadcast = true;

    /// <summary>Audio thread, under mixer lock: return at once, never call back into the mixer.</summary>
    public event EventHandler<ISampleProvider>? InputEnded;

    public float MusicForOthers { get => _musicForOthers; set => _musicForOthers = Level(value); }

    public float MicrophoneForOthers { get => _microphoneForOthers; set => _microphoneForOthers = Level(value); }

    // Monitor never carries the mic: hearing oneself late distracts.
    public float MusicForMe { get => _musicForMe; set => _musicForMe = Level(value); }

    public bool ListenToBroadcast { get => _listenToBroadcast; set => _listenToBroadcast = value; }

    public void Add(ISampleProvider input, AudioSource kind)
    {
        lock (_lock) _inputs.Add((input, kind));
    }

    /// <summary>Returns once the audio thread no longer reads the input.</summary>
    public void Remove(ISampleProvider input)
    {
        lock (_lock) _inputs.RemoveAll(entry => ReferenceEquals(entry.Input, input));
    }

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

        Clamp(broadcast);
        if (copyBroadcast)
            broadcast.CopyTo(monitor);
        else
            Clamp(monitor);
    }

    private static void Clamp(Span<float> samples)
    {
        foreach (ref var sample in samples)
            sample = Math.Clamp(sample, -1f, 1f);
    }

    private static float Level(float value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1f);
        return value;
    }
}
