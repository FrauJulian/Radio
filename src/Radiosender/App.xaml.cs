using System.Windows;
using Radiosender.Controllers;
using Radiosender.Services;

namespace Radiosender;

public partial class App : Application
{
    public static SongRepository Songs { get; private set; } = null!;

    /// <summary>App-wide, so a running broadcast keeps going while the user switches pages.</summary>
    public static ScheduleController Schedule { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = new();

    private static readonly ScheduleGenerator Generator = new();

    /// <summary>Stores the settings and applies them: alerts at once, minutes per hour from the next plan on.</summary>
    /// <exception cref="System.IO.IOException">The settings file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The settings file cannot be written.</exception>
    public static void SaveSettings(AppSettings settings)
    {
        settings.Save(AppSettings.DefaultPath);
        Settings = settings;
        Generator.Options = settings.ToScheduleOptions();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load(AppSettings.DefaultPath);
        Generator.Options = Settings.ToScheduleOptions();
        // Creates the database file and table on first start.
        Songs = new SongRepository(SongRepository.DefaultDatabasePath);
        Schedule = new ScheduleController(Songs, Generator, new PlaybackEngine(), () => DateTime.Now);
        // Follow the Windows setting: light, dark or a high-contrast theme.
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Schedule.Stop();
        base.OnExit(e);
    }
}
