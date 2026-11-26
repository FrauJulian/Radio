using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Radiosender.Controllers;
using Radiosender.Services.Audio;
using Radiosender.Services.Library;
using Radiosender.Services.Providers;
using Radiosender.Services.Scheduling;
using Radiosender.Services.Settings;
using Radiosender.Views;
using Radiosender.Views.Pages;

namespace Radiosender;

public partial class App : Application
{
    private ServiceProvider? _services;

    private static ServiceProvider BuildServices()
    {
        ServiceCollection services = new();

        // Singletons: broadcast must survive page switches.
        services.AddSingleton(_ => new SongRepository(SongRepository.DefaultDatabasePath));
        services.AddSingleton(_ => new ScheduleGenerator());
        services.AddSingleton(provider => new SettingsStore(
            provider.GetRequiredService<ScheduleGenerator>(), provider.GetRequiredService<ProgramOutput>(), AppSettings.DefaultPath));
        services.AddSingleton(_ => new PlaybackEngine());
        services.AddSingleton(_ => ProgramOutput.Instance);
        services.AddSingleton<Microphone>();
        services.AddSingleton(provider => new ScheduleController(
            provider.GetRequiredService<SongRepository>(),
            provider.GetRequiredService<ScheduleGenerator>(),
            provider.GetRequiredService<PlaybackEngine>(),
            () => DateTime.Now));
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Radiosender/1.0");
            return http;
        });
        // Import page lists providers in this order.
        services.AddSingleton<IMusicProvider, OpenverseProvider>();
        services.AddSingleton<IMusicProvider, InternetArchiveProvider>();
        services.AddSingleton<IMusicProvider>(provider => new LautFmProvider(
            provider.GetRequiredService<HttpClient>(), () => provider.GetRequiredService<SettingsStore>().Current));
        services.AddSingleton(provider => new LiveController(
            provider.GetRequiredService<ScheduleController>(),
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<ProgramOutput>(),
            provider.GetRequiredService<HttpClient>(),
            () => DateTime.Now));
        services.AddSingleton(provider => new TrackDownloader(provider.GetRequiredService<HttpClient>(), TrackDownloader.DefaultMusicFolder));
        services.AddSingleton<OnlineImportController>();
        services.AddSingleton(provider => new SongImportService(provider.GetRequiredService<SongRepository>(), SongImportService.DefaultMusicDirectory));
        services.AddSingleton<UploadController>();
        services.AddSingleton<LibraryController>();

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
        // Eager: applies settings to generator and audio output before first plan.
        _services.GetRequiredService<SettingsStore>();
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
