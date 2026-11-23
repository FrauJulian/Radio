using System.IO;
using System.Text.Json;

namespace Radiosender.Services;

/// <summary>What the user can change on the Settings page. The defaults are the values the app shipped with.</summary>
public record AppSettings
{
    public const int MinAlertSeconds = 5;
    public const int MaxAlertSeconds = 300;

    public bool NewsAlert { get; init; } = true;

    /// <summary>How long before a news item the reminder appears.</summary>
    public int NewsAlertSeconds { get; init; } = 30;

    public bool NewsAlertSound { get; init; } = true;

    public bool ModeratorAlert { get; init; } = true;

    /// <summary>How long before a moderator item the reminder appears.</summary>
    public int ModeratorAlertSeconds { get; init; } = 30;

    public bool ModeratorAlertSound { get; init; } = true;

    /// <summary>News time per hour, split over the two news at :00 and :30.</summary>
    public int NewsMinutesPerHour { get; init; } = 10;

    /// <summary>Target; the plan gets as close as the song lengths allow.</summary>
    public int ModeratorMinutesPerHour { get; init; } = 10;

    /// <summary>Target; the plan gets as close as the song lengths allow.</summary>
    public int AdMinutesPerHour { get; init; } = 20;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "settings.json");

    /// <summary>The first problem with these values as a message for the user, or <c>null</c> when they are fine.</summary>
    public string? Validate()
    {
        if (NewsAlertSeconds is < MinAlertSeconds or > MaxAlertSeconds || ModeratorAlertSeconds is < MinAlertSeconds or > MaxAlertSeconds)
            return $"Die Hinweiszeit muss zwischen {MinAlertSeconds} und {MaxAlertSeconds} Sekunden liegen.";
        if (NewsMinutesPerHour < 2)
            return "Nachrichten brauchen mindestens 2 Minuten pro Stunde.";
        if (ModeratorMinutesPerHour < 0 || AdMinutesPerHour < 0)
            return "Minuten pro Stunde dürfen nicht negativ sein.";
        if (NewsMinutesPerHour + ModeratorMinutesPerHour + AdMinutesPerHour > 60)
            return "Nachrichten, Moderation und Werbung dürfen zusammen höchstens 60 Minuten pro Stunde dauern.";
        return null;
    }

    public ScheduleOptions ToScheduleOptions() => new()
    {
        NewsDuration = TimeSpan.FromMinutes(NewsMinutesPerHour / 2.0),
        AdsPerHour = TimeSpan.FromMinutes(AdMinutesPerHour),
        ModeratorPerHour = TimeSpan.FromMinutes(ModeratorMinutesPerHour),
    };

    /// <summary>Reads the settings; a missing, unreadable or invalid file gives the defaults.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            return settings is not null && settings.Validate() is null ? settings : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
