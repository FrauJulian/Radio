using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>
/// The music library in a SQLite file, accessed through Entity Framework Core.
/// Every call uses its own short-lived context, so the repository can be used from any thread.
/// </summary>
public class SongRepository
{
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "radiosender.db");

    private readonly DbContextOptions<LibraryContext> _options;

    public SongRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _options = new DbContextOptionsBuilder<LibraryContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString())
            .Options;

        // Creates the file and the table on first start; an existing database is left as it is.
        using var context = Open();
        context.Database.EnsureCreated();
    }

    /// <summary>Ids of all provider results that are already in the library.</summary>
    public IReadOnlySet<string> GetExternalIds()
    {
        using var context = Open();
        return context.Songs.Where(s => s.ExternalId != null).Select(s => s.ExternalId!).ToHashSet();
    }

    public IReadOnlyList<Song> GetAll()
    {
        using var context = Open();
        return context.Songs.AsNoTracking().OrderBy(s => s.Artist).ThenBy(s => s.Title).ToList();
    }

    public Song? GetById(long id)
    {
        using var context = Open();
        return context.Songs.AsNoTracking().FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Inserts the song and sets its <see cref="Song.Id"/>.</summary>
    /// <exception cref="ArgumentException">The song has no duration.</exception>
    /// <exception cref="DbUpdateException">The database rejected the song, e.g. a second song with the same external id.</exception>
    public void Add(Song song)
    {
        RequireDuration(song);
        using var context = Open();
        context.Songs.Add(song);
        context.SaveChanges();
    }

    /// <summary>Stores everything except the external id, which never changes after the import.</summary>
    /// <returns><c>false</c> if no song with this id exists.</returns>
    /// <exception cref="ArgumentException">The song has no duration.</exception>
    public bool Update(Song song)
    {
        RequireDuration(song);
        using var context = Open();
        return context.Songs.Where(s => s.Id == song.Id).ExecuteUpdate(s => s
            .SetProperty(x => x.Title, song.Title)
            .SetProperty(x => x.Artist, song.Artist)
            .SetProperty(x => x.Duration, song.Duration)
            .SetProperty(x => x.FilePath, song.FilePath)
            .SetProperty(x => x.Source, song.Source)
            .SetProperty(x => x.License, song.License)
            .SetProperty(x => x.Attribution, song.Attribution)) == 1;
    }

    /// <returns><c>false</c> if no song with this id exists.</returns>
    public bool Delete(long id)
    {
        using var context = Open();
        return context.Songs.Where(s => s.Id == id).ExecuteDelete() == 1;
    }

    private LibraryContext Open() => new(_options);

    // Songs need a length for scheduling. Checked here, so the rule does not depend on the database schema.
    private static void RequireDuration(Song song)
    {
        if (song.Duration <= TimeSpan.Zero)
            throw new ArgumentException("A song needs a duration longer than zero.", nameof(song));
    }

    /// <summary>Maps <see cref="Song"/> to the table <c>Songs</c>.</summary>
    private sealed class LibraryContext(DbContextOptions<LibraryContext> options) : DbContext(options)
    {
        public DbSet<Song> Songs => Set<Song>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var song = modelBuilder.Entity<Song>();
            song.ToTable("Songs");
            // Stored as whole milliseconds in the column the first version of the app created.
            song.Property(s => s.Duration)
                .HasColumnName("DurationMs")
                .HasConversion(duration => (long)duration.TotalMilliseconds, milliseconds => TimeSpan.FromMilliseconds(milliseconds));
            // The column keeps the name it got when Openverse was the only provider.
            song.Property(s => s.ExternalId).HasColumnName("OpenverseId");
            // One song per provider result, also if two imports of the same track race.
            // SQLite allows any number of rows without an id in a unique index.
            song.HasIndex(s => s.ExternalId).IsUnique();
        }
    }
}
