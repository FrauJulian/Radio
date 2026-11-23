using Radiosender.Services;

namespace Radiosender.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radiosender-tests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Defaults_AreValidAndMatchTheOriginalSchedule()
    {
        var defaults = new AppSettings();

        Assert.Null(defaults.Validate());
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.ToScheduleOptions().NewsDuration);
        Assert.Equal(TimeSpan.FromMinutes(20), defaults.ToScheduleOptions().AdsPerHour);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.ToScheduleOptions().ModeratorPerHour);
        Assert.True(defaults.NewsAlert && defaults.ModeratorAlert);
        Assert.True(defaults.NewsAlertSound && defaults.ModeratorAlertSound);
        Assert.Equal(30, defaults.NewsAlertSeconds);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var settings = new AppSettings { NewsAlert = false, ModeratorAlertSeconds = 45, ModeratorAlertSound = true, AdMinutesPerHour = 6 };

        settings.Save(File);

        Assert.Equal(settings, AppSettings.Load(File));
    }

    [Fact]
    public void Load_FallsBackToDefaultsForMissingBrokenOrInvalidFiles()
    {
        Assert.Equal(new AppSettings(), AppSettings.Load(File));

        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Equal(new AppSettings(), AppSettings.Load(File));

        System.IO.File.WriteAllText(File, """{"AdMinutesPerHour":500}""");
        Assert.Equal(new AppSettings(), AppSettings.Load(File));
    }

    [Theory]
    [InlineData(10, 30, 20)] // exactly one hour
    [InlineData(2, 0, 0)]
    public void Validate_AcceptsUpToSixtyMinutesTogether(int news, int moderator, int ads)
    {
        Assert.Null(new AppSettings { NewsMinutesPerHour = news, ModeratorMinutesPerHour = moderator, AdMinutesPerHour = ads }.Validate());
    }

    [Theory]
    [InlineData(4, 30, 10, 10, 10)] // alert too early
    [InlineData(30, 301, 10, 10, 10)] // alert too late
    [InlineData(30, 30, 1, 10, 10)] // too little news
    [InlineData(30, 30, 10, 25, 26)] // more than 60 minutes together
    [InlineData(30, 30, 10, -1, 5)] // negative
    public void Validate_ReportsValuesOutOfRange(int newsSeconds, int moderatorSeconds, int news, int moderator, int ads)
    {
        var settings = new AppSettings
        {
            NewsAlertSeconds = newsSeconds,
            ModeratorAlertSeconds = moderatorSeconds,
            NewsMinutesPerHour = news,
            ModeratorMinutesPerHour = moderator,
            AdMinutesPerHour = ads,
        };

        Assert.NotNull(settings.Validate());
    }
}
