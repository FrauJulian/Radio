using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Controllers;

public class LibraryController(SongRepository repository)
{
    /// <summary>Returns all songs whose title or artist contains <paramref name="query"/> (case-insensitive).</summary>
    public IReadOnlyList<Song> Search(string? query)
    {
        var songs = repository.GetAll();
        if (string.IsNullOrWhiteSpace(query))
            return songs;

        query = query.Trim();
        return songs
            .Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || s.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Writes the editable metadata; file path, duration and source keep their stored values.</summary>
    /// <returns><c>false</c> if the song no longer exists.</returns>
    public bool Update(long id, string title, string artist, string? license, string? attribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);

        var song = repository.GetById(id);
        if (song is null)
            return false;

        song.Title = title.Trim();
        song.Artist = artist.Trim();
        song.License = string.IsNullOrWhiteSpace(license) ? null : license.Trim();
        song.Attribution = string.IsNullOrWhiteSpace(attribution) ? null : attribution.Trim();
        return repository.Update(song);
    }

    /// <returns><c>false</c> if the song no longer exists.</returns>
    public bool Delete(long id) => repository.Delete(id);
}
