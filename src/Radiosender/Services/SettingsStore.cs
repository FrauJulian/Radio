namespace Radiosender.Services;

/// <summary>
/// The current <see cref="AppSettings"/> for the whole app. Loads them at startup and, on every save,
/// writes the file and hands the minutes per hour to the schedule generator and the devices to the audio output.
/// </summary>
public class SettingsStore
{
    private readonly ScheduleGenerator _generator;
    private readonly ProgramOutput _output;
    private readonly string _path;

    public SettingsStore(ScheduleGenerator generator, ProgramOutput output, string path)
    {
        _generator = generator;
        _output = output;
        _path = path;
        Current = AppSettings.Load(path);
        _generator.Options = Current.ToScheduleOptions();
        // Nothing is open yet at startup, so this cannot fail here.
        _output.UseDevices(Current.BroadcastDevice, Current.MonitorDevice);
    }

    public AppSettings Current { get; private set; }

    /// <summary>Stores the settings and applies them: alerts and audio devices at once, minutes per hour from the next plan on.</summary>
    /// <exception cref="System.IO.IOException">The settings file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The settings file cannot be written.</exception>
    /// <exception cref="NAudio.MmException">A chosen audio device cannot be opened; nothing was saved.</exception>
    public void Save(AppSettings settings)
    {
        // Devices first: if one cannot be opened, the old settings stay in the file.
        if (settings.BroadcastDevice != Current.BroadcastDevice || settings.MonitorDevice != Current.MonitorDevice)
        {
            try
            {
                _output.UseDevices(settings.BroadcastDevice, settings.MonitorDevice);
            }
            catch (NAudio.MmException)
            {
                _output.UseDevices(Current.BroadcastDevice, Current.MonitorDevice);
                throw;
            }
        }
        settings.Save(_path);
        Current = settings;
        _generator.Options = settings.ToScheduleOptions();
    }
}
