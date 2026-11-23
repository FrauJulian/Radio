namespace Radiosender.Services;

/// <summary>
/// The current <see cref="AppSettings"/> for the whole app. Loads them at startup and, on every save,
/// writes the file and hands the minutes per hour to the schedule generator.
/// </summary>
public class SettingsStore
{
    private readonly ScheduleGenerator _generator;
    private readonly string _path;

    public SettingsStore(ScheduleGenerator generator, string path)
    {
        _generator = generator;
        _path = path;
        Current = AppSettings.Load(path);
        _generator.Options = Current.ToScheduleOptions();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Stores the settings and applies them: alerts at once, minutes per hour from the next plan on.</summary>
    /// <exception cref="System.IO.IOException">The settings file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The settings file cannot be written.</exception>
    public void Save(AppSettings settings)
    {
        settings.Save(_path);
        Current = settings;
        _generator.Options = settings.ToScheduleOptions();
    }
}
