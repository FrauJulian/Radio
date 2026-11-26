using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radiosender.Services.Scheduling;

namespace Radiosender.Services.Settings;

public record AppSettings
{
    private const int MinAlertSeconds = 5;
    private const int MaxAlertSeconds = 300;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public bool NewsAlert { get; init; } = true;

    public int NewsAlertSeconds { get; init; } = 30;

    public bool NewsAlertSound { get; init; } = true;

    public bool ModeratorAlert { get; init; } = true;

    public int ModeratorAlertSeconds { get; init; } = 30;

    public bool ModeratorAlertSound { get; init; } = true;

    public int NewsMinutesPerHour { get; init; } = 10;

    public int ModeratorMinutesPerHour { get; init; } = 10;
    public int AdMinutesPerHour { get; init; } = 20;

    public string? BroadcastDevice { get; init; } // null: Windows default device

    public string? MonitorDevice { get; init; } // null: no monitor device

    public bool LautFmEnabled { get; init; }

    public string LautFmStationId { get; init; } = "";

    public string LautFmHost { get; init; } = "";

    public int LautFmPort { get; init; } = 8080;

    public string LautFmMount { get; init; } = "";

    public string LautFmUser { get; init; } = "source";

    // Secrets: plain in memory, DPAPI-encrypted in the file via the Protected* properties.
    [JsonIgnore]
    public string LautFmPassword { get; init; } = "";

    [JsonIgnore]
    public string LautFmToken { get; init; } = "";

    [JsonInclude, JsonPropertyName("LautFmPassword")]
    private string? ProtectedLautFmPassword { get => Protect(LautFmPassword); init => LautFmPassword = Unprotect(value); }

    [JsonInclude, JsonPropertyName("LautFmToken")]
    private string? ProtectedLautFmToken { get => Protect(LautFmToken); init => LautFmToken = Unprotect(value); }

    // Record ToString would leak secrets into logs/debugger.
    public override string ToString() => nameof(AppSettings);

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radiosender", "settings.json");

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

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }

    // DPAPI: only this Windows user on this machine can decrypt.
    private static string? Protect(string secret) => secret.Length == 0
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    // Other user/machine: unreadable, user re-enters it.
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
