using System.IO;
using NAudio.Wave;
using Radiosender.Services;

namespace Radiosender.Tests;

/// <summary>Skips the test on machines without an audio output device (for example CI agents).</summary>
public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute()
    {
        if (WaveOut.DeviceCount == 0) Skip = "No audio output device available.";
    }
}

public sealed class MusicPlayerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));

    public MusicPlayerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        MusicPlayer.Instance.Stop();
        Directory.Delete(_directory, recursive: true);
    }

    // Writes a short, quiet sine tone as 16-bit mono PCM.
    private string CreateWav(TimeSpan length)
    {
        var path = Path.Combine(_directory, "tone.wav");
        var format = new WaveFormat(44100, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        var samples = (int)(format.SampleRate * length.TotalSeconds);
        for (var i = 0; i < samples; i++)
            writer.WriteSample(0.05f * MathF.Sin(2 * MathF.PI * 440 * i / format.SampleRate));
        return path;
    }

    [Fact]
    public void Instance_ReturnsSameObjectOnRepeatedAccess()
    {
        Assert.Same(MusicPlayer.Instance, MusicPlayer.Instance);
    }

    [Fact]
    public void Instance_ReturnsSameObjectAcrossThreads()
    {
        var instances = new MusicPlayer[16];
        Parallel.For(0, instances.Length, i => instances[i] = MusicPlayer.Instance);

        Assert.All(instances, instance => Assert.Same(MusicPlayer.Instance, instance));
    }

    [Fact]
    public void Play_MissingFile_ThrowsAndStaysStopped()
    {
        var path = Path.Combine(_directory, "missing.mp3");

        var exception = Assert.Throws<FileNotFoundException>(() => MusicPlayer.Instance.Play(path));

        Assert.Equal(path, exception.FileName);
        Assert.False(MusicPlayer.Instance.IsPlaying);
        Assert.Equal(TimeSpan.Zero, MusicPlayer.Instance.Duration);
    }

    [Fact]
    public void Play_UnreadableFile_ThrowsInvalidDataAndStaysStopped()
    {
        var path = Path.Combine(_directory, "broken.wav");
        File.WriteAllText(path, "not audio");

        Assert.Throws<InvalidDataException>(() => MusicPlayer.Instance.Play(path));
        Assert.False(MusicPlayer.Instance.IsPlaying);
    }

    [Fact]
    public void Volume_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MusicPlayer.Instance.Volume = 1.5f);
        Assert.Throws<ArgumentOutOfRangeException>(() => MusicPlayer.Instance.Volume = -0.1f);
    }

    [AudioDeviceFact]
    public void Stop_DoesNotRaiseTrackFinished()
    {
        var player = MusicPlayer.Instance;
        var finished = 0;
        EventHandler handler = (_, _) => Interlocked.Increment(ref finished);
        player.TrackFinished += handler;
        try
        {
            player.Play(CreateWav(TimeSpan.FromSeconds(0.5)));
            Assert.True(player.IsPlaying);

            player.Stop();
            Thread.Sleep(1000); // longer than the track, so a natural end would have fired by now

            Assert.Equal(0, finished);
            Assert.False(player.IsPlaying);
        }
        finally
        {
            player.TrackFinished -= handler;
        }
    }

    [AudioDeviceFact]
    public void Play_ShortWav_RaisesTrackFinishedAndPositionChanged()
    {
        var player = MusicPlayer.Instance;
        using var finished = new ManualResetEventSlim();
        var positionChanged = false;
        EventHandler finishedHandler = (_, _) => finished.Set();
        EventHandler<TimeSpan> positionHandler = (_, _) => positionChanged = true;
        player.TrackFinished += finishedHandler;
        player.PositionChanged += positionHandler;
        try
        {
            player.Play(CreateWav(TimeSpan.FromSeconds(0.6)));
            Assert.InRange(player.Duration.TotalSeconds, 0.59, 0.61);

            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)), "TrackFinished was not raised.");
            Assert.True(positionChanged);
            Assert.False(player.IsPlaying);
            Assert.Equal(TimeSpan.Zero, player.Duration);
        }
        finally
        {
            player.TrackFinished -= finishedHandler;
            player.PositionChanged -= positionHandler;
        }
    }
}
