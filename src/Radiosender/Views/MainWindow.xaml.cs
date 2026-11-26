using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Localization;
using Radiosender.Services.Settings;
using Radiosender.Views.Pages;
using Wpf.Ui.Controls;

namespace Radiosender.Views;

public partial class MainWindow : FluentWindow
{
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ScheduleController _schedule;
    private readonly SettingsStore _settings;
    private DateTime _remindedFor;

    public MainWindow(IServiceProvider services, ScheduleController schedule, SettingsStore settings)
    {
        _schedule = schedule;
        _settings = settings;
        InitializeComponent();
        RootNavigation.SetServiceProvider(services);
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(SchedulePage));
        _reminderTimer.Tick += (_, _) => RemindSpeaker();
        _reminderTimer.Start();
    }

    private void RemindSpeaker()
    {
        var settings = _settings.Current;
        TimeSpan? newsLead = settings.NewsAlert ? TimeSpan.FromSeconds(settings.NewsAlertSeconds) : null;
        TimeSpan? moderatorLead = settings.ModeratorAlert ? TimeSpan.FromSeconds(settings.ModeratorAlertSeconds) : null;
        if (_schedule.UpcomingSpeech(newsLead, moderatorLead) is not { } item || item.Start == _remindedFor)
            return;
        _remindedFor = item.Start; // once per item
        var isNews = item.Kind == ScheduleItemKind.News;
        var what = item.KindName;
        if (isNews ? settings.NewsAlertSound : settings.ModeratorAlertSound)
            PlayBing();
        _ = new Wpf.Ui.Controls.MessageBox
        {
            Title = Loc.T("Main_ReminderTitle"),
            Content = item.Title.Length > 0 ? Loc.F("Main_ReminderTextTitled", what, item.Start, item.Title) : Loc.F("Main_ReminderText", what, item.Start),
            CloseButtonText = Loc.T("Main_Ok"),
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Topmost = true,
        }.ShowDialogAsync();
    }

    private static void PlayBing()
    {
        try
        {
            var tone = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = 880, Gain = 0.25 }
                .Take(TimeSpan.FromMilliseconds(350));
            var output = new WaveOut();
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Init(tone);
            output.Play();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            // No audio device: dialog still reminds.
        }
    }
}
