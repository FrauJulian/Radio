using System.IO;
using System.Runtime.CompilerServices;
using Radiosender.Models;
using Radiosender.Services.Library;
using Radiosender.Services.Localization;
using Radiosender.Services.Providers;

namespace Radiosender.Controllers;

public class OnlineImportController(IEnumerable<IMusicProvider> providers, TrackDownloader downloader, SongRepository songs)
{
    public IReadOnlyList<IMusicProvider> Providers { get; } = [.. providers];

    public IReadOnlySet<string> ImportedIds() => songs.GetExternalIds();

    // All providers at once; each result as soon as its provider is done. One failure never stops the others.
    public async IAsyncEnumerable<ProviderResult> SearchAllAsync(string query, IReadOnlySet<string>? licenses,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var search in Task.WhenEach(Providers.Select(provider => SearchOneAsync(provider, query, licenses, cancellationToken))))
            yield return await search;
    }

    private static async Task<ProviderResult> SearchOneAsync(IMusicProvider provider, string query, IReadOnlySet<string>? licenses, CancellationToken cancellationToken)
    {
        try
        {
            return new(provider, await provider.SearchAsync(query, licenses, cancellationToken));
        }
        catch (MusicProviderException ex)
        {
            return new(provider, [], ex);
        }
    }

    /// <exception cref="MusicProviderException">Track not importable or download failed.</exception>
    public async Task<Song> ImportAsync(MusicTrack track, CancellationToken cancellationToken = default)
    {
        if (track.Duration is not { } duration)
            throw new MusicProviderException(Loc.T("Provider_NoDuration"));
        if (track.Id.Length > 0 && ImportedIds().Contains(track.Id))
            throw new MusicProviderException(Loc.F("Provider_AlreadyInLibrary", track.Title));

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
