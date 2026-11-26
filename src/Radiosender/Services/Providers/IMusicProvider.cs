using Radiosender.Models;

namespace Radiosender.Services.Providers;

// New provider: implement and register in App; import page lists all registered.
public interface IMusicProvider
{
    string Name { get; }

    bool IsAvailable => true;

    /// <exception cref="MusicProviderException">Search failed.</exception>
    Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
