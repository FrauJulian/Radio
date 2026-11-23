using Radiosender.Services;

namespace Radiosender.Tests;

public class PlaceholderContentTests
{
    [Fact]
    public void AdSpots_AreNonEmptyWithTitlesAndPositiveDurations()
    {
        Assert.NotEmpty(PlaceholderContent.AdSpots);
        Assert.All(PlaceholderContent.AdSpots, ad =>
        {
            Assert.False(string.IsNullOrWhiteSpace(ad.Title));
            Assert.True(ad.Duration > TimeSpan.Zero);
        });
    }

    [Fact]
    public void ModeratorSegments_AreNonEmptyWithTitlesAndPositiveDurations()
    {
        Assert.NotEmpty(PlaceholderContent.ModeratorSegments);
        Assert.All(PlaceholderContent.ModeratorSegments, segment =>
        {
            Assert.False(string.IsNullOrWhiteSpace(segment.Title));
            Assert.True(segment.Duration > TimeSpan.Zero);
        });
    }
}
