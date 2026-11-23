using System.Windows;
using Radiosender.Services;

namespace Radiosender;

public partial class App : Application
{
    public static SongRepository Songs { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Creates the database file and table on first start.
        Songs = new SongRepository(SongRepository.DefaultDatabasePath);
        base.OnStartup(e);
    }
}
