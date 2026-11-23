using System.IO;
using System.Windows;
using System.Windows.Controls;
using Radiosender.Services;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

/// <summary>Edits <see cref="AppSettings"/>. Nothing changes until Save.</summary>
public partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    // Loaded fires on every navigation to the page, so unsaved edits from an earlier visit are dropped.
    private void OnLoaded(object sender, RoutedEventArgs e) => Show(App.Settings);

    private void Show(AppSettings settings)
    {
        NewsAlertSwitch.IsChecked = settings.NewsAlert;
        NewsSecondsBox.Value = settings.NewsAlertSeconds;
        NewsSoundBox.IsChecked = settings.NewsAlertSound;
        ModeratorAlertSwitch.IsChecked = settings.ModeratorAlert;
        ModeratorSecondsBox.Value = settings.ModeratorAlertSeconds;
        ModeratorSoundBox.IsChecked = settings.ModeratorAlertSound;
        NewsMinutesBox.Value = settings.NewsMinutesPerHour;
        ModeratorMinutesBox.Value = settings.ModeratorMinutesPerHour;
        AdMinutesBox.Value = settings.AdMinutesPerHour;
    }

    // An emptied number box counts as its default.
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
            App.SaveSettings(settings);
            ShowMessage(InfoBarSeverity.Success, "Gespeichert", "Minuten pro Stunde gelten für den nächsten Plan.");
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
