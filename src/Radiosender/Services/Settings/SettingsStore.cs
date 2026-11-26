using Radiosender.Services.Audio;
using Radiosender.Services.Scheduling;

namespace Radiosender.Services.Settings;

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
        // Nothing open at startup: cannot fail.
        _output.UseDevices(Current.BroadcastDevice, Current.MonitorDevice);
    }

    public AppSettings Current { get; private set; }

    /// <exception cref="System.IO.IOException">File not writable.</exception>
    /// <exception cref="UnauthorizedAccessException">File not writable.</exception>
    /// <exception cref="NAudio.MmException">Device cannot be opened; nothing saved.</exception>
    public void Save(AppSettings settings)
    {
        // Devices first: on failure the file keeps the old settings.
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
