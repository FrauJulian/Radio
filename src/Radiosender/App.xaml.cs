using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Radiosender.Controllers;
using Radiosender.Services;
using Radiosender.Views;
using Radiosender.Views.Pages;

namespace Radiosender;

/// <summary>Composition root: wires every service, controller and page once, at startup.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // One instance each for the whole app. The schedule controller in particular must be shared,
        // so a running broadcast keeps going while the user switches pages.
        services.AddSingleton(_ => new SongRepository(SongRepository.DefaultDatabasePath));
        services.AddSingleton(_ => new ScheduleGenerator());
        services.AddSingleton(provider => new SettingsStore(
            provider.GetRequiredService<ScheduleGenerator>(), provider.GetRequiredService<ProgramOutput>(), AppSettings.DefaultPath));
        services.AddSingleton(_ => new PlaybackEngine());
        // The one audio channel that songs and the microphone share.
        services.AddSingleton(_ => ProgramOutput.Instance);
        services.AddSingleton<Microphone>();
        services.AddSingleton(provider => new ScheduleController(
            provider.GetRequiredService<SongRepository>(),
            provider.GetRequiredService<ScheduleGenerator>(),
            provider.GetRequiredService<PlaybackEngine>(),
            () => DateTime.Now));
        // One HttpClient for all providers and downloads.
        services.AddSingleton(_ =>
        {
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Radiosender/1.0");
            return http;
        });
        // Music providers: add a line here for each new IMusicProvider; the import page lists them all.
        services.AddSingleton<IMusicProvider, OpenverseProvider>();
        services.AddSingleton<IMusicProvider, InternetArchiveProvider>();
        // The station's own library; listed only while laut.fm is switched on in the settings.
        services.AddSingleton<IMusicProvider>(provider => new LautFmProvider(
            provider.GetRequiredService<System.Net.Http.HttpClient>(), () => provider.GetRequiredService<SettingsStore>().Current));
        services.AddSingleton(provider => new LiveController(
            provider.GetRequiredService<ScheduleController>(),
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<ProgramOutput>(),
            provider.GetRequiredService<System.Net.Http.HttpClient>(),
            () => DateTime.Now));
        services.AddSingleton(provider => new TrackDownloader(provider.GetRequiredService<System.Net.Http.HttpClient>(), TrackDownloader.DefaultMusicFolder));
        services.AddSingleton<OnlineImportController>();
        services.AddSingleton(provider => new SongImportService(provider.GetRequiredService<SongRepository>(), SongImportService.DefaultMusicDirectory));
        services.AddSingleton<UploadController>();
        services.AddSingleton<LibraryController>();

        // Pages are created anew on every navigation, as before; they hold only view state.
        services.AddSingleton<MainWindow>();
        services.AddTransient<SchedulePage>();
        services.AddTransient<LibraryPage>();
        services.AddTransient<ImportPage>();
        services.AddTransient<SettingsPage>();

        return services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = BuildServices();
        // Loads the settings: the generator gets its minutes per hour before the first plan, the audio output its devices.
        _services.GetRequiredService<SettingsStore>();
        // Follow the Windows setting: light, dark or a high-contrast theme.
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetRequiredService<LiveController>().Stop();
        _services?.GetRequiredService<ScheduleController>().Stop();
        _services?.Dispose();
        base.OnExit(e);
    }
}
