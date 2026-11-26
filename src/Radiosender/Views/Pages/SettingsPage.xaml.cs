using System.IO;
using System.Windows;
using System.Windows.Controls;
using Radiosender.Services.Audio;
using Radiosender.Services.Settings;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class SettingsPage : Page
{
    private readonly SettingsStore _store;

    public SettingsPage(SettingsStore store)
    {
        _store = store;
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Show(_store.Current);

    // Index 0 of each list = no device name (null).
    private const string DefaultDevice = "Windows-Standardgerät";
    private const string NoDevice = "Kein eigenes Gerät";

    private void Show(AppSettings settings)
    {
        var devices = ProgramOutput.DeviceNames().ToList();
        BroadcastDeviceBox.ItemsSource = devices.Prepend(DefaultDevice).ToList();
        MonitorDeviceBox.ItemsSource = devices.Prepend(NoDevice).ToList();
        BroadcastDeviceBox.SelectedIndex = devices.IndexOf(settings.BroadcastDevice ?? "") + 1;
        MonitorDeviceBox.SelectedIndex = devices.IndexOf(settings.MonitorDevice ?? "") + 1;
        NewsAlertSwitch.IsChecked = settings.NewsAlert;
        NewsSecondsBox.Value = settings.NewsAlertSeconds;
        NewsSoundBox.IsChecked = settings.NewsAlertSound;
        ModeratorAlertSwitch.IsChecked = settings.ModeratorAlert;
        ModeratorSecondsBox.Value = settings.ModeratorAlertSeconds;
        ModeratorSoundBox.IsChecked = settings.ModeratorAlertSound;
        NewsMinutesBox.Value = settings.NewsMinutesPerHour;
        ModeratorMinutesBox.Value = settings.ModeratorMinutesPerHour;
        AdMinutesBox.Value = settings.AdMinutesPerHour;
        LautFmSwitch.IsChecked = settings.LautFmEnabled;
        LautFmHostBox.Text = settings.LautFmHost;
        LautFmPortBox.Value = settings.LautFmPort;
        LautFmMountBox.Text = settings.LautFmMount;
        LautFmUserBox.Text = settings.LautFmUser;
        LautFmPasswordBox.Password = settings.LautFmPassword;
        LautFmStationBox.Text = settings.LautFmStationId;
        LautFmTokenBox.Password = settings.LautFmToken;
        OnLautFmChanged(this, new RoutedEventArgs());
    }

    private void OnLautFmChanged(object sender, RoutedEventArgs e) =>
        LautFmFields.Visibility = LautFmSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // Empty number box -> default.
    private static AppSettings Defaults { get; } = new();

    private AppSettings Read() => new()
    {
        NewsAlert = NewsAlertSwitch.IsChecked == true,
        NewsAlertSeconds = (int)(NewsSecondsBox.Value ?? Defaults.NewsAlertSeconds),
        NewsAlertSound = NewsSoundBox.IsChecked == true,
        ModeratorAlert = ModeratorAlertSwitch.IsChecked == true,
        ModeratorAlertSeconds = (int)(ModeratorSecondsBox.Value ?? Defaults.ModeratorAlertSeconds),
        ModeratorAlertSound = ModeratorSoundBox.IsChecked == true,
        NewsMinutesPerHour = (int)(NewsMinutesBox.Value ?? Defaults.NewsMinutesPerHour),
        ModeratorMinutesPerHour = (int)(ModeratorMinutesBox.Value ?? Defaults.ModeratorMinutesPerHour),
        AdMinutesPerHour = (int)(AdMinutesBox.Value ?? Defaults.AdMinutesPerHour),
        BroadcastDevice = BroadcastDeviceBox.SelectedIndex > 0 ? (string)BroadcastDeviceBox.SelectedItem : null,
        MonitorDevice = MonitorDeviceBox.SelectedIndex > 0 ? (string)MonitorDeviceBox.SelectedItem : null,
        LautFmEnabled = LautFmSwitch.IsChecked == true,
        LautFmHost = LautFmHostBox.Text.Trim(),
        LautFmPort = (int)(LautFmPortBox.Value ?? Defaults.LautFmPort),
        LautFmMount = LautFmMountBox.Text.Trim(),
        LautFmUser = LautFmUserBox.Text.Trim(),
        LautFmPassword = LautFmPasswordBox.Password,
        LautFmStationId = LautFmStationBox.Text.Trim(),
        LautFmToken = LautFmTokenBox.Password.Trim(),
    };

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = Read();
        if (settings.Validate() is { } problem)
        {
            ShowMessage(InfoBarSeverity.Error, "Nicht gespeichert", problem);
            return;
        }

        try
        {
            _store.Save(settings);
            ShowMessage(InfoBarSeverity.Success, "Gespeichert", "Minuten pro Stunde gelten für den nächsten Plan.");
        }
        catch (NAudio.MmException)
        {
            ShowMessage(InfoBarSeverity.Error, "Nicht gespeichert", "Ein gewähltes Audiogerät konnte nicht geöffnet werden. Die bisherigen Geräte bleiben aktiv.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(InfoBarSeverity.Error, "Nicht gespeichert", ex.Message);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        Show(Defaults);
        ShowMessage(InfoBarSeverity.Informational, "Standardwerte eingesetzt", "Mit Speichern übernehmen.");
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
