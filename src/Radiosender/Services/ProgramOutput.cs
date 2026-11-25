using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Radiosender.Services;

/// <summary>
/// The station's audio channel: everything that goes on air (songs, the microphone) is mixed here.
/// The broadcast mix plays on one device for the listeners. A second device, if one is chosen, plays
/// the monitor mix for the person at the desk; without it the one device plays the monitor mix.
/// Cues for the speaker only, like the alert tone and the import preview, stay off this channel.
/// </summary>
public sealed class ProgramOutput
{
    private static readonly Lazy<ProgramOutput> LazyInstance = new(() => new ProgramOutput());

    /// <summary>All inputs are converted to this format before mixing.</summary>
    public static WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly Lock _lock = new();
    private string? _broadcastName;
    private string? _monitorName;
    private bool _started;
    private WaveOut? _broadcastDevice;
    private WaveOut? _monitorDevice;

    private ProgramOutput()
    {
        Mixer.InputEnded += (_, input) => InputEnded?.Invoke(this, input);
        // Releases the audio devices when the app exits.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (_lock) CloseDevices(); };
    }

    public static ProgramOutput Instance => LazyInstance.Value;

    /// <summary>The levels of both mixes.</summary>
    public ProgramMixer Mixer { get; } = new();

    /// <summary>Same as <see cref="ProgramMixer.InputEnded"/>: raised on the audio thread, handlers must return at once.</summary>
    public event EventHandler<ISampleProvider>? InputEnded;

    /// <summary>Names of the playback devices Windows offers, for the settings page.</summary>
    public static IReadOnlyList<string> DeviceNames() =>
        Enumerable.Range(0, WaveOut.DeviceCount).Select(i => WaveOut.GetCapabilities(i).ProductName).ToList();

    /// <summary>
    /// Chooses the devices by name: <c>null</c> for the broadcast means the Windows default device,
    /// <c>null</c> for the monitor means no device of its own. Devices that are already open are reopened.
    /// A name that no longer exists counts as <c>null</c>.
    /// </summary>
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

    /// <summary>Puts a source on air and opens the output devices on first use.</summary>
    /// <returns>The input as the mixer knows it; pass it to <see cref="Remove"/> and compare it in <see cref="InputEnded"/>.</returns>
    /// <exception cref="NotSupportedException">The source has more than two channels.</exception>
    /// <exception cref="NAudio.MmException">No audio output device can be opened.</exception>
    public ISampleProvider Add(ISampleProvider source, AudioSource kind)
    {
        var input = ToProgramFormat(source);
        lock (_lock)
        {
            if (!_started)
            {
                OpenDevices();
                _started = true;
            }
        }
        Mixer.Add(input, kind);
        return input;
    }

    /// <summary>Takes an input off air. Returns once the audio thread no longer reads from it.</summary>
    public void Remove(ISampleProvider input) => Mixer.Remove(input);

    /// <summary>Converts mono to stereo and any sample rate to the channel's 44.1 kHz.</summary>
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
    private void OpenDevices()
    {
        var names = DeviceNames().ToList();
        var broadcast = _broadcastName is null ? -1 : names.IndexOf(_broadcastName); // -1 is the Windows default device
        var monitor = _monitorName is null ? -1 : names.IndexOf(_monitorName);
        // The same device twice would only play both mixes on top of each other.
        if (monitor == broadcast || (broadcast == -1 && monitor == 0 && names.Count == 1))
            monitor = -1;

        try
        {
            if (monitor < 0)
            {
                // One device: the person at the desk hears the monitor mix; nothing is sent anywhere else.
                _broadcastDevice = Open(broadcast, new Tap(Mixer, null));
            }
            else
            {
                // The broadcast device sets the pace. Each block it asks for also yields the monitor block,
                // which waits in a short buffer for the second device. The buffer drops what it cannot hold,
                // so the two device clocks drifting apart never builds up a delay.
                var monitorBuffer = new BufferedWaveProvider(Format, TimeSpan.FromMilliseconds(300)) { DiscardOnBufferOverflow = true };
                _broadcastDevice = Open(broadcast, new Tap(Mixer, monitorBuffer));
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
        // Three buffers of 50 ms: short enough that live speech does not lag noticeably.
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

    /// <summary>
    /// What the pacing device plays. With a monitor buffer it plays the broadcast mix and feeds the buffer;
    /// without one it plays the monitor mix itself.
    /// </summary>
    private sealed class Tap(ProgramMixer mixer, BufferedWaveProvider? monitorBuffer) : ISampleProvider
    {
        private float[] _other = [];

        public WaveFormat WaveFormat => Format;

        public int Read(Span<float> buffer)
        {
            if (_other.Length < buffer.Length)
                _other = new float[buffer.Length];
            var other = _other.AsSpan(0, buffer.Length);

            if (monitorBuffer is null)
            {
                mixer.Mix(other, buffer);
            }
            else
            {
                mixer.Mix(buffer, other);
                monitorBuffer.AddSamples(MemoryMarshal.AsBytes(other));
            }
            return buffer.Length; // never ends: silence while nothing is on air
        }
    }
}
