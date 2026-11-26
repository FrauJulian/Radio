using System.IO;
using Radiosender.Models;
using Radiosender.Services.Library;
using Radiosender.Services.Providers;

namespace Radiosender.Controllers;

public class OnlineImportController(IEnumerable<IMusicProvider> providers, TrackDownloader downloader, SongRepository songs)
{
    private readonly IReadOnlyList<IMusicProvider> _providers = [.. providers];

    public IReadOnlyList<IMusicProvider> Providers => [.. _providers.Where(provider => provider.IsAvailable)];

    public IReadOnlySet<string> ImportedIds() => songs.GetExternalIds();

    /// <exception cref="MusicProviderException">Track not importable or download failed.</exception>
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
