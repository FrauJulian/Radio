using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Radiosender.Services.Audio;

// On-air channel only; speaker-only cues (alert tone, import preview) stay off it.
public sealed class ProgramOutput
{
    private static readonly Lazy<ProgramOutput> LazyInstance = new(() => new ProgramOutput());

    public static WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly Lock _lock = new();
    private string? _broadcastName;
    private string? _monitorName;
    private bool _started;
    private WaveOut? _broadcastDevice;
    private WaveOut? _monitorDevice;
    private volatile BufferedWaveProvider? _liveFeed;

    private ProgramOutput()
    {
        Mixer.InputEnded += (_, input) => InputEnded?.Invoke(this, input);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (_lock) CloseDevices(); };
    }

    public static ProgramOutput Instance => LazyInstance.Value;

    public ProgramMixer Mixer { get; } = new();

    /// <summary>Audio thread: handlers must return at once.</summary>
    public event EventHandler<ISampleProvider>? InputEnded;

    public static IReadOnlyList<string> DeviceNames() =>
        [.. Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName)];

    /// <exception cref="NAudio.MmException">A device cannot be opened.</exception>
    public void UseDevices(string? broadcast, string? monitor)
    {
        lock (_lock)
        {
            _broadcastName = broadcast;
            _monitorName = monitor;
            if (!_started)
                return;
            CloseDevices();
            OpenDevices();
        }
    }

    /// <exception cref="NotSupportedException">More than two channels.</exception>
    /// <exception cref="NAudio.MmException">No output device can be opened.</exception>
    public ISampleProvider Add(ISampleProvider source, AudioSource kind)
    {
        var input = ToProgramFormat(source);
        lock (_lock) EnsureStarted();
        Mixer.Add(input, kind);
        return input;
    }

    /// <exception cref="NAudio.MmException">No output device can be opened.</exception>
    public BufferedWaveProvider OpenLiveFeed()
    {
        var feed = new BufferedWaveProvider(Format, TimeSpan.FromSeconds(5)) { DiscardOnBufferOverflow = true, ReadFully = false };
        lock (_lock)
        {
            EnsureStarted();
            _liveFeed = feed;
        }
        return feed;
    }

    public void CloseLiveFeed(BufferedWaveProvider feed)
    {
        lock (_lock)
        {
            if (_liveFeed == feed)
                _liveFeed = null;
        }
    }

    public void Remove(ISampleProvider input) => Mixer.Remove(input);

    public static ISampleProvider ToProgramFormat(ISampleProvider source)
    {
        if (source.WaveFormat.Channels > 2)
            throw new NotSupportedException("Audio with more than two channels is not supported.");
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.SampleRate != Format.SampleRate)
            source = new WdlResamplingSampleProvider(source, Format.SampleRate);
        return source;
    }

    // Caller holds _lock.
    private void EnsureStarted()
    {
        if (_started)
            return;
        OpenDevices();
        _started = true;
    }

    // Caller holds _lock.
    private void OpenDevices()
    {
        var names = DeviceNames().ToList();
        var broadcast = _broadcastName is null ? -1 : names.IndexOf(_broadcastName); // -1 = Windows default
        var monitor = _monitorName is null ? -1 : names.IndexOf(_monitorName);
        if (monitor == broadcast || (broadcast == -1 && monitor == 0 && names.Count == 1))
            monitor = -1;

        try
        {
            if (monitor < 0)
            {
                _broadcastDevice = Open(broadcast, new Tap(this, null));
            }
            else
            {
                // Broadcast device paces; monitor gets a short dropping buffer so clock drift never adds delay.
                var monitorBuffer = new BufferedWaveProvider(Format, TimeSpan.FromMilliseconds(300)) { DiscardOnBufferOverflow = true };
                _broadcastDevice = Open(broadcast, new Tap(this, monitorBuffer));
                _monitorDevice = Open(monitor, monitorBuffer.ToSampleProvider());
            }
        }
        catch
        {
            CloseDevices();
            throw;
        }
    }

    private static WaveOut Open(int deviceNumber, ISampleProvider source)
    {
        // 3 x 50 ms: live speech must not lag noticeably.
        var device = new WaveOut { DeviceNumber = deviceNumber, BufferMilliseconds = 50, NumberOfBuffers = 3 };
        try
        {
            device.Init(source);
            device.Play();
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    // Caller holds _lock.
    private void CloseDevices()
    {
        _broadcastDevice?.Dispose();
        _broadcastDevice = null;
        _monitorDevice?.Dispose();
        _monitorDevice = null;
    }

    // Pacing device source. With buffer: plays broadcast, feeds monitor. Without: plays monitor. Broadcast always to live feed.
    private sealed class Tap(ProgramOutput output, BufferedWaveProvider? monitorBuffer) : ISampleProvider
    {
        private float[] _other = [];

        public WaveFormat WaveFormat => Format;

        public int Read(Span<float> buffer)
        {
            if (_other.Length < buffer.Length)
                _other = new float[buffer.Length];
            var other = _other.AsSpan(0, buffer.Length);

            Span<float> broadcast;
            if (monitorBuffer is null)
            {
                output.Mixer.Mix(other, buffer);
                broadcast = other;
            }
            else
            {
                output.Mixer.Mix(buffer, other);
                monitorBuffer.AddSamples(MemoryMarshal.AsBytes(other));
                broadcast = buffer;
            }
            output._liveFeed?.AddSamples(MemoryMarshal.AsBytes(broadcast));
            return buffer.Length; // never ends: silence while nothing is on air
        }
    }
}
