using Radiosender.Models;
using Radiosender.Services.Providers;

namespace Radiosender.Controllers;

// Error set: this provider failed, Tracks empty.
public record ProviderResult(IMusicProvider Provider, IReadOnlyList<MusicTrack> Tracks, MusicProviderException? Error = null);
