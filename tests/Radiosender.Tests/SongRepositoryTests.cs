using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Radiosender.Models;
using Radiosender.Services.Library;

namespace Radiosender.Tests;

public sealed class SongRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "test.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // pooled connections keep the file locked
        Directory.Delete(_directory, recursive: true);
    }

    private static Song NewSong(string title = "Song") => new()
    {
        Title = title,
        Artist = "Artist",
        Duration = TimeSpan.FromSeconds(185.5),
        FilePath = @"C:\music\song.mp3",
        Source = SongSource.Online,
        License = "CC BY 4.0",
        Attribution = null,
    };

    [Fact]
    public void Constructor_CreatesDatabaseFileAndDirectory()
    {
        _ = new SongRepository(DatabasePath);

        Assert.True(File.Exists(DatabasePath));
    }

    [Fact]
    public void Add_ThenGetById_ReturnsSameValues()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong();

        repository.Add(song);
        var loaded = repository.GetById(song.Id);

        Assert.True(song.Id > 0);
        Assert.NotNull(loaded);
        Assert.Equal(song.Title, loaded.Title);
        Assert.Equal(song.Artist, loaded.Artist);
        Assert.Equal(song.Duration, loaded.Duration);
        Assert.Equal(song.FilePath, loaded.FilePath);
        Assert.Equal(song.Source, loaded.Source);
        Assert.Equal(song.License, loaded.License);
        Assert.Null(loaded.Attribution);
    }

    [Fact]
    public void Data_SurvivesNewRepositoryInstance()
    {
        new SongRepository(DatabasePath).Add(NewSong("A"));

        var songs = new SongRepository(DatabasePath).GetAll();

        Assert.Equal("A", Assert.Single(songs).Title);
    }

    [Fact]
    public void Update_ChangesStoredSong()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong();
        repository.Add(song);

        song.Title = "Renamed";
        song.Attribution = "by someone";

        Assert.True(repository.Update(song));
        var loaded = repository.GetById(song.Id)!;
        Assert.Equal("Renamed", loaded.Title);
        Assert.Equal("by someone", loaded.Attribution);
    }

    [Fact]
    public void Delete_RemovesSong()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong();
        repository.Add(song);

        Assert.True(repository.Delete(song.Id));
        Assert.Null(repository.GetById(song.Id));
        Assert.Empty(repository.GetAll());
    }

    [Fact]
    public void UpdateAndDelete_ReturnFalseForUnknownId()
    {
        var repository = new SongRepository(DatabasePath);

        var unknown = NewSong();
        unknown.Id = 42;

        Assert.False(repository.Update(unknown));
        Assert.False(repository.Delete(42));
    }

    [Fact]
    public void Add_StoresTitleWithSqlCharactersAsText()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong("x'); DROP TABLE Songs; --");

        repository.Add(song);

        Assert.Equal(song.Title, Assert.Single(repository.GetAll()).Title);
    }

    [Fact]
    public void Add_RejectsZeroDuration()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong();
        song.Duration = TimeSpan.Zero;

        Assert.Throws<ArgumentException>(() => repository.Add(song));
        Assert.Empty(repository.GetAll());
    }

    [Fact]
    public void ExternalId_IsStoredAndListed()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong();
        song.ExternalId = "ov-1";
        repository.Add(song);
        repository.Add(NewSong("Uploaded"));

        Assert.Equal("ov-1", repository.GetById(song.Id)!.ExternalId);
        Assert.Equal(["ov-1"], repository.GetExternalIds());
    }

    [Fact]
    public void ExternalId_CannotBeStoredTwice()
    {
        var repository = new SongRepository(DatabasePath);
        repository.Add(new Song { Title = "A", Artist = "", Duration = TimeSpan.FromSeconds(1), FilePath = "a", ExternalId = "ov-1" });

        Assert.Throws<DbUpdateException>(() =>
            repository.Add(new Song { Title = "B", Artist = "", Duration = TimeSpan.FromSeconds(1), FilePath = "b", ExternalId = "ov-1" }));
    }

    [Fact]
    public void Update_RejectsZeroDurationAndKeepsTheExternalId()
    {
        var repository = new SongRepository(DatabasePath);
        var song = NewSong();
        song.ExternalId = "ov-1";
        repository.Add(song);

        song.Title = "Renamed";
        song.ExternalId = "changed";
        Assert.True(repository.Update(song));
        Assert.Equal("ov-1", repository.GetById(song.Id)!.ExternalId);

        song.Duration = TimeSpan.Zero;
        Assert.Throws<ArgumentException>(() => repository.Update(song));
    }

    // The schema below is what the app created before it used Entity Framework Core; libraries from then must keep working.
    [Fact]
    public void Constructor_OpensADatabaseFromBeforeEntityFramework()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Songs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT NOT NULL, Artist TEXT NOT NULL,
                    DurationMs INTEGER NOT NULL CHECK (DurationMs > 0), FilePath TEXT NOT NULL,
                    Source INTEGER NOT NULL, License TEXT, Attribution TEXT, OpenverseId TEXT);
                CREATE UNIQUE INDEX IX_Songs_OpenverseId ON Songs (OpenverseId) WHERE OpenverseId IS NOT NULL;
                INSERT INTO Songs (Title, Artist, DurationMs, FilePath, Source, License, OpenverseId)
                VALUES ('Old', 'Artist', 185500, 'old.mp3', 1, 'CC BY 4.0', 'ov-old');
                """;
            command.ExecuteNonQuery();
        }

        var repository = new SongRepository(DatabasePath);
        var old = Assert.Single(repository.GetAll());
        Assert.Equal("Old", old.Title);
        Assert.Equal(TimeSpan.FromSeconds(185.5), old.Duration);
        Assert.Equal(SongSource.Online, old.Source);
        Assert.Equal("ov-old", old.ExternalId);

        var added = NewSong("New");
        repository.Add(added);
        old.Title = "Renamed";
        Assert.True(repository.Update(old));
        Assert.Equal(["New", "Renamed"], repository.GetAll().Select(s => s.Title).Order());
        Assert.True(repository.Delete(added.Id));
    }}
