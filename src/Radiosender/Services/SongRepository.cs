using System.IO;
using Microsoft.Data.Sqlite;
using Radiosender.Models;

namespace Radiosender.Services;

public class SongRepository
{
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "radiosender.db");

    private const string Columns = "Id, Title, Artist, DurationMs, FilePath, Source, License, Attribution";

    private readonly string _connectionString;

    public SongRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Songs (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Title       TEXT    NOT NULL,
                Artist      TEXT    NOT NULL,
                DurationMs  INTEGER NOT NULL CHECK (DurationMs > 0),
                FilePath    TEXT    NOT NULL,
                Source      INTEGER NOT NULL,
                License     TEXT,
                Attribution TEXT
            )
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Song> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Songs ORDER BY Artist, Title";
        using var reader = command.ExecuteReader();
        var songs = new List<Song>();
        while (reader.Read())
            songs.Add(Read(reader));
        return songs;
    }

    public Song? GetById(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Songs WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Inserts the song and sets its <see cref="Song.Id"/>.</summary>
    public void Add(Song song)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Songs (Title, Artist, DurationMs, FilePath, Source, License, Attribution)
            VALUES ($title, $artist, $durationMs, $filePath, $source, $license, $attribution);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, song);
        song.Id = (long)command.ExecuteScalar()!;
    }

    /// <returns><c>false</c> if no song with this id exists.</returns>
    public bool Update(Song song)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Songs SET Title = $title, Artist = $artist, DurationMs = $durationMs, FilePath = $filePath,
                Source = $source, License = $license, Attribution = $attribution
            WHERE Id = $id
            """;
        AddParameters(command, song);
        command.Parameters.AddWithValue("$id", song.Id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <returns><c>false</c> if no song with this id exists.</returns>
    public bool Delete(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Songs WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddParameters(SqliteCommand command, Song song)
    {
        command.Parameters.AddWithValue("$title", song.Title);
        command.Parameters.AddWithValue("$artist", song.Artist);
        command.Parameters.AddWithValue("$durationMs", (long)song.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$filePath", song.FilePath);
        command.Parameters.AddWithValue("$source", (int)song.Source);
        command.Parameters.AddWithValue("$license", (object?)song.License ?? DBNull.Value);
        command.Parameters.AddWithValue("$attribution", (object?)song.Attribution ?? DBNull.Value);
    }

    private static Song Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Title = reader.GetString(1),
        Artist = reader.GetString(2),
        Duration = TimeSpan.FromMilliseconds(reader.GetInt64(3)),
        FilePath = reader.GetString(4),
        Source = (SongSource)reader.GetInt32(5),
        License = reader.IsDBNull(6) ? null : reader.GetString(6),
        Attribution = reader.IsDBNull(7) ? null : reader.GetString(7),
    };
}
