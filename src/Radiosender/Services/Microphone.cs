using NAudio.Wave;

namespace Radiosender.Services;

/// <summary>Puts the default recording device on air through the <see cref="ProgramOutput"/>, so the speaker can talk over or between items.</summary>
public sealed class Microphone(ProgramOutput output) : IDisposable
{
    private readonly Lock _lock = new();
    private WaveIn? _capture;
    private ISampleProvider? _input;

    public bool IsOn
    {
        get { lock (_lock) return _capture is not null; }
    }

    /// <exception cref="InvalidOperationException">No microphone or no output device can be opened; the message can be shown to the user.</exception>
    public void TurnOn()
    {
        lock (_lock)
        {
            if (_capture is not null)
                return;
            if (WaveIn.DeviceCount == 0)
                throw new InvalidOperationException("Es wurde kein Mikrofon gefunden.");

            var capture = new WaveIn { WaveFormat = new WaveFormat(44100, 16, 1), BufferMilliseconds = 30 };
            // A short buffer that drops what it cannot hold: speech must not fall further and further behind.
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
                throw new InvalidOperationException($"Das Mikrofon konnte nicht eingeschaltet werden: {e.Message}", e);
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
            _capture?.Dispose(); // stops the recording
            _capture = null;
        }
    }

    public void Dispose() => TurnOff();
}
