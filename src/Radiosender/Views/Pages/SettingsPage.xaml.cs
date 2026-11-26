using System.IO;
using System.Windows;
using System.Windows.Controls;
using Radiosender.Services.Audio;
using Radiosender.Services.Localization;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.Changed -= OnLanguageChanged;
        Loc.Changed += OnLanguageChanged;
        Show(_store.Current);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Loc.Changed -= OnLanguageChanged;

    // Re-fill the code-set first entries, keep the selection.
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var (broadcast, monitor) = (BroadcastDeviceBox.SelectedIndex, MonitorDeviceBox.SelectedIndex);
        FillDevices();
        BroadcastDeviceBox.SelectedIndex = broadcast;
        MonitorDeviceBox.SelectedIndex = monitor;
    }

    private List<string> _devices = [];

    // Index 0 of each list = no device name (null).
    private void FillDevices()
    {
        BroadcastDeviceBox.ItemsSource = _devices.Prepend(Loc.T("Settings_DefaultDevice")).ToList();
        MonitorDeviceBox.ItemsSource = _devices.Prepend(Loc.T("Settings_NoDevice")).ToList();
    }

    private void Show(AppSettings settings)
    {
        _devices = ProgramOutput.DeviceNames().ToList();
        var devices = _devices;
        FillDevices();
        BroadcastDeviceBox.SelectedIndex = devices.IndexOf(settings.BroadcastDevice ?? "") + 1;
        MonitorDeviceBox.SelectedIndex = devices.IndexOf(settings.MonitorDevice ?? "") + 1;
        LanguageBox.SelectedIndex = settings.Language == Loc.English ? 1 : 0;
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
        OnLautFmChanged(this, new RoutedEventArgs());
    }

    private void OnLautFmChanged(object sender, RoutedEventArgs e) =>
        LautFmFields.Visibility = LautFmSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // Empty number box -> default.
    private static AppSettings Defaults { get; } = new();

    private AppSettings Read() => new()
    {
        Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? Loc.German,
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
    };

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = Read();
        if (settings.Validate() is { } problem)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.T("Settings_NotSaved"), problem);
            return;
        }

        try
        {
            _store.Save(settings);
            Loc.SetLanguage(settings.Language);
            ShowMessage(InfoBarSeverity.Success, Loc.T("Settings_Saved"), Loc.T("Settings_SavedDetail"));
        }
        catch (NAudio.MmException)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.T("Settings_NotSaved"), Loc.T("Settings_AudioDeviceFailed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.T("Settings_NotSaved"), ex.Message);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        Show(Defaults);
        ShowMessage(InfoBarSeverity.Informational, Loc.T("Settings_DefaultsApplied"), Loc.T("Settings_DefaultsApplyHint"));
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        if (severity == InfoBarSeverity.Error)
        {
            Message.IsOpen = false;
            ErrorPopup.Show(this, title, text);
            return;
        }
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
