using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Radiosender.Models;

namespace Radiosender.Services.Library;

// Fresh DbContext per call: safe from any thread.
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

        using var context = Open();
        context.Database.EnsureCreated();
    }

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

    /// <exception cref="DbUpdateException">Rejected, e.g. duplicate external id.</exception>
    public void Add(Song song)
    {
        RequireDuration(song);
        using var context = Open();
        context.Songs.Add(song);
        context.SaveChanges();
    }

    // ExternalId never changes after import.
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

    public bool Delete(long id)
    {
        using var context = Open();
        return context.Songs.Where(s => s.Id == id).ExecuteDelete() == 1;
    }

    private LibraryContext Open() => new(_options);

    private static void RequireDuration(Song song)
    {
        if (song.Duration <= TimeSpan.Zero)
            throw new ArgumentException("A song needs a duration longer than zero.", nameof(song));
    }

    private sealed class LibraryContext(DbContextOptions<LibraryContext> options) : DbContext(options)
    {
        public DbSet<Song> Songs => Set<Song>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var song = modelBuilder.Entity<Song>();
            song.ToTable("Songs");
            // Legacy column names from v1; keep for existing databases.
            song.Property(s => s.Duration)
                .HasColumnName("DurationMs")
                .HasConversion(duration => (long)duration.TotalMilliseconds, milliseconds => TimeSpan.FromMilliseconds(milliseconds));
            song.Property(s => s.ExternalId).HasColumnName("OpenverseId");
            // Unique also under racing imports; SQLite allows many NULLs.
            song.HasIndex(s => s.ExternalId).IsUnique();
        }
    }
}
