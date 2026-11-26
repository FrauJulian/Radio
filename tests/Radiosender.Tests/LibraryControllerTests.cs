using Microsoft.Data.Sqlite;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Library;

namespace Radiosender.Tests;

public sealed class LibraryControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));
    private readonly SongRepository _repository;
    private readonly LibraryController _controller;

    public LibraryControllerTests()
    {
        _repository = new SongRepository(Path.Combine(_directory, "test.db"));
        _controller = new LibraryController(_repository);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // pooled connections keep the file locked
        Directory.Delete(_directory, recursive: true);
    }

    private Song AddSong(string title, string artist)
    {
        var song = new Song
        {
            Title = title,
            Artist = artist,
            Duration = TimeSpan.FromSeconds(200),
            FilePath = @"C:\music\song.mp3",
            Source = SongSource.Upload,
            License = "CC BY 4.0",
        };
        _repository.Add(song);
        return song;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_WithoutQuery_ReturnsAllSongs(string? query)
    {
        AddSong("Alpha", "Band");
        AddSong("Beta", "Other");

        Assert.Equal(2, _controller.Search(query).Count);
    }

    [Fact]
    public void Search_MatchesTitleOrArtistCaseInsensitive()
    {
        AddSong("Morning Light", "Band");
        AddSong("Evening", "The Lights");
        AddSong("Unrelated", "Nobody");

        var titles = _controller.Search(" LIGHT ").Select(s => s.Title).ToList();

        Assert.Equal(["Evening", "Morning Light"], titles.Order());
    }

    [Fact]
    public void Update_WritesEditableFieldsAndKeepsTheRest()
    {
        var song = AddSong("Old", "Old Artist");

        Assert.True(_controller.Update(song.Id, " New ", "New Artist", "  ", "by someone"));

        var loaded = _repository.GetById(song.Id)!;
        Assert.Equal("New", loaded.Title);
        Assert.Equal("New Artist", loaded.Artist);
        Assert.Null(loaded.License);
        Assert.Equal("by someone", loaded.Attribution);
        Assert.Equal(song.FilePath, loaded.FilePath);
        Assert.Equal(song.Duration, loaded.Duration);
        Assert.Equal(song.Source, loaded.Source);
    }

    [Fact]
    public void Update_RejectsBlankTitleOrArtist()
    {
        var song = AddSong("Title", "Artist");

        Assert.ThrowsAny<ArgumentException>(() => _controller.Update(song.Id, " ", "Artist", null, null));
        Assert.ThrowsAny<ArgumentException>(() => _controller.Update(song.Id, "Title", "", null, null));
        Assert.Equal("Title", _repository.GetById(song.Id)!.Title);
    }

    [Fact]
    public void Update_ReturnsFalseForUnknownSong()
    {
        Assert.False(_controller.Update(42, "Title", "Artist", null, null));
    }

    [Fact]
    public void Delete_RemovesSong()
    {
        var song = AddSong("Title", "Artist");

        Assert.True(_controller.Delete(song.Id));
        Assert.Empty(_controller.Search(null));
        Assert.False(_controller.Delete(song.Id));
    }
}
