using NAudio.Wave;
using Radiosender.Services;

namespace Radiosender.Tests;

public class ProgramMixerTests
{
    /// <summary>Delivers the same sample value for a given number of samples, then ends.</summary>
    private sealed class Constant(float value, int samples = int.MaxValue) : ISampleProvider
    {
        private int _left = samples;

        public WaveFormat WaveFormat => ProgramOutput.Format;

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, _left);
            buffer[..count].Fill(value);
            _left -= count;
            return count;
        }
    }

    private static (float Broadcast, float Monitor) MixOnce(ProgramMixer mixer)
    {
        Span<float> broadcast = stackalloc float[8];
        Span<float> monitor = stackalloc float[8];
        mixer.Mix(broadcast, monitor);
        return (broadcast[0], monitor[0]);
    }

    private static ProgramMixer WithMusicAndMicrophone()
    {
        var mixer = new ProgramMixer { ListenToBroadcast = false };
        mixer.Add(new Constant(0.4f), AudioSource.Music);
        mixer.Add(new Constant(0.2f), AudioSource.Microphone);
        return mixer;
    }

    [Fact]
    public void Mix_AppliesEachLevelToItsOwnMix()
    {
        var mixer = WithMusicAndMicrophone();
        mixer.MusicForOthers = 0.5f;
        mixer.MicrophoneForOthers = 1f;
        mixer.MusicForMe = 0.25f;

        var (broadcast, monitor) = MixOnce(mixer);

        Assert.Equal(0.4f * 0.5f + 0.2f, broadcast, 5);
        Assert.Equal(0.4f * 0.25f, monitor, 5); // the monitor never carries the microphone
    }

    [Fact]
    public void Mix_ListeningToTheBroadcastCopiesItExactly()
    {
        var mixer = WithMusicAndMicrophone();
        mixer.MusicForOthers = 0.5f;
        mixer.MusicForMe = 0f;
        mixer.ListenToBroadcast = true;

        var (broadcast, monitor) = MixOnce(mixer);

        Assert.Equal(0.4f, broadcast, 5);
        Assert.Equal(broadcast, monitor);
    }

    [Fact]
    public void Mix_LimitsTheSumToFullScale()
    {
        var mixer = new ProgramMixer();
        mixer.Add(new Constant(0.8f), AudioSource.Music);
        mixer.Add(new Constant(0.8f), AudioSource.Microphone);

        Assert.Equal(1f, MixOnce(mixer).Broadcast);
    }

    [Fact]
    public void Mix_DropsAnInputThatEndedAndReportsIt()
    {
        var mixer = new ProgramMixer();
        var track = new Constant(0.5f, samples: 4);
        ISampleProvider? ended = null;
        mixer.InputEnded += (_, input) => ended = input;
        mixer.Add(track, AudioSource.Music);

        Span<float> broadcast = stackalloc float[8];
        Span<float> monitor = stackalloc float[8];
        mixer.Mix(broadcast, monitor);

        Assert.Same(track, ended);
        Assert.Equal(0.5f, broadcast[3]);
        Assert.Equal(0f, broadcast[4]); // silence after the end of the track
        Assert.Equal(0f, MixOnce(mixer).Broadcast);
    }

    [Fact]
    public void Remove_TakesTheInputOffBothMixes()
    {
        var mixer = new ProgramMixer();
        var track = new Constant(0.5f);
        mixer.Add(track, AudioSource.Music);

        mixer.Remove(track);

        Assert.Equal((0f, 0f), MixOnce(mixer));
    }

    [Fact]
    public void Levels_OutOfRange_Throw()
    {
        var mixer = new ProgramMixer();

        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.MusicForMe = 1.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.MicrophoneForOthers = -0.1f);
    }
}
