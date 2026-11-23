using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Radiosender.Models;
using Radiosender.Views.Pages;
using Wpf.Ui.Controls;

namespace Radiosender.Views;

public partial class MainWindow : FluentWindow
{
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _remindedFor;

    public MainWindow()
    {
        InitializeComponent();
        // Re-themes the app when the user switches the Windows theme while it runs.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(SchedulePage));
        _reminderTimer.Tick += (_, _) => RemindSpeaker();
        _reminderTimer.Start();
    }

    // Shortly before news or moderator talk goes on air, the person at the microphone gets a heads-up.
    private void RemindSpeaker()
    {
        var settings = App.Settings;
        TimeSpan? newsLead = settings.NewsAlert ? TimeSpan.FromSeconds(settings.NewsAlertSeconds) : null;
        TimeSpan? moderatorLead = settings.ModeratorAlert ? TimeSpan.FromSeconds(settings.ModeratorAlertSeconds) : null;
        if (App.Schedule.UpcomingSpeech(newsLead, moderatorLead) is not { } item || item.Start == _remindedFor)
            return;
        _remindedFor = item.Start; // once per item
        var isNews = item.Kind == ScheduleItemKind.News;
        var what = item.KindName;
        if (isNews ? settings.NewsAlertSound : settings.ModeratorAlertSound)
            PlayBing();
        // A dialog in the middle of the screen that stays until OK is clicked; the broadcast keeps running behind it.
        _ = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Gleich bist du dran.",
            Content = item.Title.Length > 0 ? $"{what} um {item.Start:HH:mm:ss}: {item.Title}" : $"{what} um {item.Start:HH:mm:ss}",
            CloseButtonText = "OK",
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Topmost = true,
        }.ShowDialogAsync();
    }

    // A short generated tone on its own output, so it sounds over the running broadcast.
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
            // No audio device: the dialog still reminds the speaker.
        }
    }
}
