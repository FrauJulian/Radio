using System.Windows;
using Radiosender.Controllers;
using Radiosender.Services;

namespace Radiosender;

public partial class App : Application
{
    public static SongRepository Songs { get; private set; } = null!;

    /// <summary>App-wide, so a running broadcast keeps going while the user switches pages.</summary>
    public static ScheduleController Schedule { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Creates the database file and table on first start.
        Songs = new SongRepository(SongRepository.DefaultDatabasePath);
        Schedule = new ScheduleController(Songs, new ScheduleGenerator(), new PlaybackEngine(), () => DateTime.Now);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Schedule.Stop();
        base.OnExit(e);
    }
}
