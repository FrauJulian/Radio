using System.Globalization;
using System.Windows;
using Radiosender.Models;
using Wpf.Ui.Controls;

namespace Radiosender.Views;

/// <summary>
/// Dialog for the title of a news, ad or moderator item and, for news and moderator items, its length.
/// Only collects input; the caller changes the schedule.
/// </summary>
public partial class ScheduleItemEditWindow : FluentWindow
{
    private static readonly string[] DurationFormats = [@"m\:ss", @"mm\:ss"];

    public ScheduleItemEditWindow(ScheduleItem item)
    {
        InitializeComponent();
        Title = Bar.Title = $"{item.KindName} bearbeiten";
        TitleBox.Text = item.Title;
        DurationBox.Text = item.Duration.ToString(@"m\:ss");
        Duration = item.Duration;
        // Ads keep their length.
        DurationLabel.IsEnabled = DurationBox.IsEnabled = item.Kind is ScheduleItemKind.News or ScheduleItemKind.Moderator;
    }

    public string ItemTitle => TitleBox.Text;

    public TimeSpan Duration { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DurationBox.IsEnabled)
        {
            if (!TimeSpan.TryParseExact(DurationBox.Text.Trim(), DurationFormats, CultureInfo.InvariantCulture, out var duration)
                || duration <= TimeSpan.Zero)
            {
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            Duration = duration;
        }

        DialogResult = true;
    }
}
