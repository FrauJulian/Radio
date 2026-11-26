using System.IO;
using NAudio.Wave;
using Radiosender.Services.Audio;

namespace Radiosender.Tests;

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
    private string CreateWav(TimeSpan length, string name = "tone.wav")
    {
        var path = Path.Combine(_directory, name);
        var format = new WaveFormat(44100, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        var samples = (int)(format.SampleRate * length.TotalSeconds);
        for (var i = 0; i < samples; i++)
            writer.WriteSample(0.05f * MathF.Sin(2 * MathF.PI * 440 * i / format.SampleRate));
        return path;
    }

    [Fact]
    public void ProgramOutput_ConvertsInputsToItsFormat()
    {
        var mono = new NAudio.Wave.SampleProviders.SignalGenerator(22050, 1);

        var converted = ProgramOutput.ToProgramFormat(mono);

        Assert.Equal(44100, converted.WaveFormat.SampleRate);
        Assert.Equal(2, converted.WaveFormat.Channels);
        Assert.Same(ProgramOutput.Instance, ProgramOutput.Instance);
        Assert.Throws<NotSupportedException>(() => ProgramOutput.ToProgramFormat(new NAudio.Wave.SampleProviders.SignalGenerator(44100, 6)));
    }

    [AudioDeviceFact]
    public void Play_TwoTracksInARow_RaisesTrackFinishedOnlyForTheSecond()
    {
        var player = MusicPlayer.Instance;
        var finished = 0;
        using var done = new ManualResetEventSlim();
        EventHandler handler = (_, _) => { Interlocked.Increment(ref finished); done.Set(); };
        player.TrackFinished += handler;
        try
        {
            player.Play(CreateWav(TimeSpan.FromSeconds(2)));
            Thread.Sleep(200);
            player.Play(CreateWav(TimeSpan.FromSeconds(0.5), "second.wav"));

            Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(300);
            Assert.Equal(1, finished);
        }
        finally
        {
            player.TrackFinished -= handler;
        }
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
    public void Play_ShortWav_RaisesTrackFinished()
    {
        var player = MusicPlayer.Instance;
        using var finished = new ManualResetEventSlim();
        EventHandler finishedHandler = (_, _) => finished.Set();
        player.TrackFinished += finishedHandler;
        try
        {
            player.Play(CreateWav(TimeSpan.FromSeconds(0.6)));
            Assert.InRange(player.Duration.TotalSeconds, 0.59, 0.61);

            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)), "TrackFinished was not raised.");
            Assert.False(player.IsPlaying);
            Assert.Equal(TimeSpan.Zero, player.Duration);
        }
        finally
        {
            player.TrackFinished -= finishedHandler;
        }
    }
}
