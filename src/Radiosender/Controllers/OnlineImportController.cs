using System.IO;
using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Controllers;

/// <summary>Searches the registered music providers and imports their results into the library.</summary>
public class OnlineImportController(IEnumerable<IMusicProvider> providers, TrackDownloader downloader, SongRepository songs)
{
    /// <summary>Every provider registered in <c>App</c>, in registration order.</summary>
    public IReadOnlyList<IMusicProvider> Providers { get; } = providers.ToList();

    /// <summary>Ids of the provider results that are already in the library.</summary>
    public IReadOnlySet<string> ImportedIds() => songs.GetExternalIds();

    /// <summary>Downloads the track and stores it as a song with its license and attribution.</summary>
    /// <exception cref="MusicProviderException">The track cannot be imported or the download failed.</exception>
    public async Task<Song> ImportAsync(MusicTrack track, CancellationToken cancellationToken = default)
    {
        if (track.Duration is not { } duration)
            throw new MusicProviderException("Dieses Ergebnis hat keine Dauer und kann nicht importiert werden.");
        if (track.Id.Length > 0 && ImportedIds().Contains(track.Id))
            throw new MusicProviderException($"\"{track.Title}\" ist bereits in der Bibliothek.");

        var path = await downloader.DownloadAsync(track, cancellationToken);
        var song = new Song
        {
            Title = track.Title,
            Artist = string.IsNullOrWhiteSpace(track.Creator) ? "Unknown" : track.Creator,
            Duration = duration,
            FilePath = path,
            Source = SongSource.Online,
            License = track.License,
            Attribution = track.Attribution,
            ExternalId = track.Id.Length > 0 ? track.Id : null,
        };
        try
        {
            songs.Add(song);
        }
        catch
        {
            File.Delete(path); // no orphaned file without a database entry
            throw;
        }
        return song;
    }
}
