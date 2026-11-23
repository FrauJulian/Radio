using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class SchedulePage : Page
{
    private readonly ScheduleController _controller = App.Schedule;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;
    private int _scrolledTo = -1;

    public SchedulePage()
    {
        InitializeComponent();
        // Also refreshes the buttons: Finished can arrive just before the engine reports it stopped.
        _progressTimer.Tick += (_, _) =>
        {
            UpdateButtons();
            UpdateProgress();
        };
    }

    // The page can be shown again while a broadcast runs, so it always rebuilds from the controller.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _controller.ItemStarted += OnItemStarted;
        _controller.ItemFailed += OnItemFailed;
        _controller.Finished += OnFinished;

        _loading = true;
        VolumeSlider.Value = _controller.Volume;
        _loading = false;

        // The library can change on other pages.
        RequestSong.ItemsSource = _controller.LibrarySongs()
            .OrderBy(s => s.Artist).ThenBy(s => s.Title)
            .Select(s => new SongOption(s.Id, $"{(string.IsNullOrWhiteSpace(s.Artist) ? s.Title : $"{s.Artist} - {s.Title}")} ({s.Duration:m\\:ss})"))
            .ToList();

        ShowCurrent();
        _progressTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _controller.ItemStarted -= OnItemStarted;
        _controller.ItemFailed -= OnItemFailed;
        _controller.Finished -= OnFinished;
        _progressTimer.Stop();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var hours = (int)(HoursBox.Value ?? 1);
        try
        {
            _controller.CreatePlan(hours);
            var plan = _controller.Plan;
            ShowMessage(InfoBarSeverity.Success, "Schedule created",
                $"{hours} h from {plan[0].Start:HH:mm} to {plan[^1].End:HH:mm}, {plan.Count} items.");
        }
        catch (Exception ex) when (ex is ScheduleException or ArgumentOutOfRangeException)
        {
            ShowMessage(InfoBarSeverity.Error, "Could not create the schedule", ex.Message);
        }
        ShowCurrent();
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedItem is not TimelineRow row || RequestSong.SelectedItem is not SongOption song)
            return;
        try
        {
            var plan = _controller.Swap(row.Index, song.Id);
            var placed = plan[row.Index];
            ShowMessage(InfoBarSeverity.Success, "Song request planned",
                $"\"{placed.Title}\" plays at {placed.Start:HH:mm:ss} instead of \"{row.Item.Title}\". The rest of that half hour was replanned, the news stays on time.");
        }
        catch (ScheduleException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Could not play the request", ex.Message);
        }
        ShowCurrent();
    }

    private void OnRequestChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnStart(object sender, RoutedEventArgs e)
    {
        Message.IsOpen = false;
        _controller.Start();
        ShowCurrent();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _controller.Stop();
        ShowCurrent();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading) _controller.Volume = (float)e.NewValue;
    }

    // Engine events arrive on a pool thread. BeginInvoke, not Invoke: Stop waits for that thread.
    private void OnItemStarted(object? sender, int index) => Dispatcher.BeginInvoke(ShowCurrent);

    private void OnItemFailed(object? sender, string message) =>
        Dispatcher.BeginInvoke(() => ShowMessage(InfoBarSeverity.Warning, "Item skipped", message));

    private void OnFinished(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ShowMessage(InfoBarSeverity.Informational, "Broadcast finished", "The whole schedule has been played.");
        ShowCurrent();
    });

    // Rebuilds the rows from the controller; the selection is the user's swap target and is kept.
    private void ShowCurrent()
    {
        var item = _controller.CurrentItem;
        var current = _controller.CurrentIndex;
        var plan = _controller.Plan;

        var selected = Timeline.SelectedIndex;
        var rows = plan.Select((p, i) => new TimelineRow(i, p, i == current)).ToList();
        Timeline.ItemsSource = rows;
        Timeline.SelectedIndex = selected < rows.Count ? selected : -1;
        // Follow the broadcast when it moves on; otherwise keep the user's row in view.
        if (current != _scrolledTo && current >= 0 && current < rows.Count)
            Timeline.ScrollIntoView(rows[current]);
        else if (Timeline.SelectedItem is { } row)
            Timeline.ScrollIntoView(row);
        _scrolledTo = current;
        EmptyText.Visibility = plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        NowKind.Content = item;
        NowKind.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        NowTitle.Text = item?.Title ?? "Nothing on air";

        UpdateButtons();
        UpdateProgress();
    }

    private void UpdateButtons()
    {
        var playing = _controller.IsPlaying;
        StartButton.IsEnabled = !playing && _controller.Plan.Count > 0;
        StopButton.IsEnabled = playing;

        var row = Timeline.SelectedItem as TimelineRow;
        var blocker = row switch
        {
            null => "Select an upcoming item in the schedule.",
            { Item.Kind: ScheduleItemKind.News } => "News cannot be replaced.",
            _ when playing && row.Index <= _controller.CurrentIndex => "This item is already on air or over.",
            _ when RequestSong.SelectedItem is null => "Choose a song from the library.",
            _ => null,
        };
        SwapHint.Text = blocker ?? $"Replaces the item at {row!.Item.Start:HH:mm:ss}.";
        SwapButton.IsEnabled = blocker is null;
    }

    private void UpdateProgress()
    {
        if (_controller.CurrentItem is not { } item)
        {
            NowProgress.Value = 0;
            NowTime.Text = " ";
            return;
        }

        var elapsed = _controller.CurrentElapsed;
        NowProgress.Value = item.Duration.Ticks == 0 ? 0 : (double)elapsed.Ticks / item.Duration.Ticks;
        NowTime.Text = $"{elapsed:m\\:ss} / {item.Duration:m\\:ss}  ·  planned {item.Start:HH:mm:ss}";
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }

    private sealed record TimelineRow(int Index, ScheduleItem Item, bool IsCurrent);

    private sealed record SongOption(long Id, string Label);
}
