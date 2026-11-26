using Radiosender.Models;

namespace Radiosender.Services.Providers;

// New provider: implement and register in App; import page lists all registered.
public interface IMusicProvider
{
    string Name { get; }

    // licenses: LicenseType codes to keep; null = all.
    /// <exception cref="MusicProviderException">Search failed.</exception>
    Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, IReadOnlySet<string>? licenses = null, CancellationToken cancellationToken = default);
}
