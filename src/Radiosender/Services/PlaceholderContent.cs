using Radiosender.Models;

namespace Radiosender.Services;

// In-memory seed data; varied lengths let the schedule generator fill gaps before the news.
public static class PlaceholderContent
{
    public static IReadOnlyList<AdSpot> AdSpots { get; } =
    [
        new("Bakery Sonnenschein", TimeSpan.FromSeconds(30)),
        new("Autohaus Berger", TimeSpan.FromSeconds(45)),
        new("Stadtwerke Energy Tips", TimeSpan.FromSeconds(60)),
        new("Furniture Outlet Sale", TimeSpan.FromSeconds(75)),
        new("Regional Bank Savings", TimeSpan.FromSeconds(90)),
        new("Summer Festival Preview", TimeSpan.FromMinutes(2)),
    ];

    public static IReadOnlyList<ModeratorSegment> ModeratorSegments { get; } =
    [
        new("Station ID", TimeSpan.FromSeconds(30)),
        new("Weather Update", TimeSpan.FromSeconds(60)),
        new("Traffic Report", TimeSpan.FromSeconds(90)),
        new("Song Introduction", TimeSpan.FromMinutes(2)),
        new("Local Events", TimeSpan.FromSeconds(150)),
        new("Listener Greetings", TimeSpan.FromMinutes(3)),
    ];
}
