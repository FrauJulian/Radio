using NAudio.Wave;
using Radiosender.Services.Localization;

namespace Radiosender.Services.Audio;

public sealed class Microphone(ProgramOutput output) : IDisposable
{
    private readonly Lock _lock = new();
    private WaveIn? _capture;
    private ISampleProvider? _input;

    public bool IsOn
    {
        get { lock (_lock) return _capture is not null; }
    }

    /// <exception cref="InvalidOperationException">No mic or output device; message is user-facing.</exception>
    public void TurnOn()
    {
        lock (_lock)
        {
            if (_capture is not null)
                return;
            if (WaveIn.DeviceCount == 0)
                throw new InvalidOperationException(Loc.T("Audio_NoMicrophone"));

            var capture = new WaveIn { WaveFormat = new WaveFormat(44100, 16, 1), BufferMilliseconds = 30 };
            // Short dropping buffer: speech must not drift further behind.
            var buffer = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromMilliseconds(300)) { DiscardOnBufferOverflow = true };
            capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            try
            {
                capture.StartRecording();
                _input = output.Add(buffer.ToSampleProvider(), AudioSource.Microphone);
            }
            catch (Exception e) when (e is NAudio.MmException or NotSupportedException)
            {
                capture.Dispose();
                throw new InvalidOperationException(Loc.F("Audio_MicrophoneFailed", e.Message), e);
            }
            _capture = capture;
        }
    }

    public void TurnOff()
    {
        lock (_lock)
        {
            if (_input is not null)
                output.Remove(_input);
            _input = null;
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Dispose() => TurnOff();
}
