using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Playback device for the listeners; <c>null</c> is the Windows default device.</summary>
    public string? BroadcastDevice { get; init; }

    /// <summary>Playback device for the person at the desk; <c>null</c> means no device of its own.</summary>
    public string? MonitorDevice { get; init; }

    /// <summary>Streaming to laut.fm and its library on the Import page; the Live button shows only while this is on.</summary>
    public bool LautFmEnabled { get; init; }

    /// <summary>The station's id in Radioadmin, needed for the library.</summary>
    public string LautFmStationId { get; init; } = "";

    /// <summary>Live access data from Radioadmin, "Live senden".</summary>
    public string LautFmHost { get; init; } = "";

    public int LautFmPort { get; init; } = 8080;

    public string LautFmMount { get; init; } = "";

    public string LautFmUser { get; init; } = "source";

    /// <summary>Kept in memory as is; the file only holds it encrypted for the Windows user.</summary>
    [JsonIgnore]
    public string LautFmPassword { get; init; } = "";

    /// <summary>The Radioadmin login token for the library; stored encrypted like the password.</summary>
    [JsonIgnore]
    public string LautFmToken { get; init; } = "";

    [JsonInclude, JsonPropertyName("LautFmPassword")]
    private string? ProtectedLautFmPassword { get => Protect(LautFmPassword); init => LautFmPassword = Unprotect(value); }

    [JsonInclude, JsonPropertyName("LautFmToken")]
    private string? ProtectedLautFmToken { get => Protect(LautFmToken); init => LautFmToken = Unprotect(value); }

    // Records print every property; the password and token stay out of logs and debugger views.
    public override string ToString() => nameof(AppSettings);

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
        if (LautFmEnabled)
        {
            if (string.IsNullOrWhiteSpace(LautFmHost) || Uri.CheckHostName(LautFmHost.Trim()) == UriHostNameType.Unknown)
                return "Für laut.fm fehlt ein gültiger Server.";
            if (LautFmPort is < 1 or > 65535)
                return "Der laut.fm-Port muss zwischen 1 und 65535 liegen.";
            if (string.IsNullOrWhiteSpace(LautFmMount))
                return "Für laut.fm fehlt der Mountpoint.";
            if (LautFmToken.Length > 0 && string.IsNullOrWhiteSpace(LautFmStationId))
                return "Für die laut.fm-Bibliothek fehlt die Stations-ID.";
        }
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

    // DPAPI: only this Windows user on this computer can read the value back.
    private static string? Protect(string secret) => secret.Length == 0
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    // A value from another user or computer cannot be read; the user enters it again.
    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
