using System.IO;
using Radiosender.Models;
using Radiosender.Services;

namespace Radiosender.Controllers;

public class OpenverseController(OpenverseClient client, SongRepository songs)
{
    public Task<IReadOnlyList<OpenverseTrack>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        client.SearchAsync(query, cancellationToken);

    /// <summary>Downloads the track and stores it as a song with its license and attribution.</summary>
    public async Task<Song> ImportAsync(OpenverseTrack track, CancellationToken cancellationToken = default)
    {
        if (track.Duration is not { } duration)
            throw new OpenverseException("This result has no duration and cannot be imported.");

        var path = await client.DownloadAsync(track, cancellationToken);
        var song = new Song
        {
            Title = track.Title,
            Artist = string.IsNullOrWhiteSpace(track.Creator) ? "Unknown" : track.Creator,
            Duration = duration,
            FilePath = path,
            Source = SongSource.Openverse,
            License = track.LicenseName,
            Attribution = track.Attribution,
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
