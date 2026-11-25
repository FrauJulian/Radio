using Radiosender.Models;

namespace Radiosender.Services;

/// <summary>Error of a music provider or a download, with a message that can be shown to the user as is.</summary>
public class MusicProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A catalog of music that can be searched online. To add one, implement this interface and register the
/// class in <c>App</c>; the import page lists every registered provider. Downloading and storing the
/// results is the same for all providers and not part of this interface.
/// </summary>
public interface IMusicProvider
{
    /// <summary>Name shown to the user, for example "Openverse".</summary>
    string Name { get; }

    /// <summary>Whether the import page offers this provider now; one that needs a setting can hide itself.</summary>
    bool IsAvailable => true;

    /// <exception cref="MusicProviderException">The search failed.</exception>
    Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
