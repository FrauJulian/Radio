using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Tests;

public sealed class SongImportServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));
    private readonly SongRepository _repository;
    private readonly SongImportService _service;

    private string SourceDirectory => Path.Combine(_directory, "source");
    private string MusicDirectory => Path.Combine(_directory, "music");

    public SongImportServiceTests()
    {
        Directory.CreateDirectory(SourceDirectory);
        _repository = new SongRepository(Path.Combine(_directory, "test.db"));
        _service = new SongImportService(_repository, MusicDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // pooled connections keep the file locked
        Directory.Delete(_directory, recursive: true);
    }

    private string CreateWav(string fileName, TimeSpan duration)
    {
        var path = Path.Combine(SourceDirectory, fileName);
        var format = new WaveFormat(8000, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.Write(new byte[(int)(format.AverageBytesPerSecond * duration.TotalSeconds)]);
        return path;
    }

    private string[] MusicFiles() =>
        Directory.Exists(MusicDirectory) ? Directory.GetFiles(MusicDirectory) : [];

    [Fact]
    public void Import_Wav_CopiesFileAndSavesSong()
    {
        var source = CreateWav("My Song.wav", TimeSpan.FromSeconds(2));

        var song = _service.Import(source);

        var stored = _repository.GetById(song.Id);
        Assert.NotNull(stored);
        Assert.Equal("My Song", stored.Title);
        Assert.Equal(TimeSpan.FromSeconds(2), stored.Duration);
        Assert.Equal(SongSource.Upload, stored.Source);
        Assert.Equal(Path.Combine(MusicDirectory, "My Song.wav"), stored.FilePath);
        Assert.True(File.Exists(stored.FilePath));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Import_SameFileTwice_DoesNotOverwrite()
    {
        var source = CreateWav("song.wav", TimeSpan.FromSeconds(1));

        var first = _service.Import(source);
        var second = _service.Import(source);

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal(Path.Combine(MusicDirectory, "song (2).wav"), second.FilePath);
        Assert.Equal(2, MusicFiles().Length);
    }

    [Fact]
    public void Import_WrongExtension_IsRejected()
    {
        var source = Path.Combine(SourceDirectory, "notes.txt");
        File.WriteAllText(source, "not audio");

        Assert.Throws<SongImportException>(() => _service.Import(source));
        Assert.Empty(MusicFiles());
        Assert.Empty(_repository.GetAll());
    }

    [Theory]
    [InlineData("garbage.wav")]
    [InlineData("garbage.mp3")]
    public void Import_GarbageFile_IsRejectedWithoutLeftoverCopy(string fileName)
    {
        var source = Path.Combine(SourceDirectory, fileName);
        File.WriteAllBytes(source, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());

        Assert.Throws<SongImportException>(() => _service.Import(source));
        Assert.Empty(MusicFiles());
        Assert.Empty(_repository.GetAll());
    }

    [Fact]
    public void Import_EmptyWav_IsRejectedWithoutLeftoverCopy()
    {
        var source = CreateWav("silence.wav", TimeSpan.Zero);

        Assert.Throws<SongImportException>(() => _service.Import(source));
        Assert.Empty(MusicFiles());
    }

    [Fact]
    public void Import_MissingFile_IsRejected()
    {
        Assert.Throws<SongImportException>(() => _service.Import(Path.Combine(SourceDirectory, "missing.mp3")));
        Assert.Empty(MusicFiles());
    }
}
