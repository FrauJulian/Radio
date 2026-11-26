using NAudio.Wave;

namespace Radiosender.Tests;

/// <summary>Skips the test on machines without an audio output device (for example CI agents).</summary>
public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute()
    {
        if (WaveOut.DeviceCount == 0) Skip = "No audio output device available.";
    }
}
