using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Radiosender.Controllers;
using Radiosender.Services;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class SchedulePage : Page
{
    private readonly ScheduleController _controller = App.Schedule;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;

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

        ShowPlan();
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
        ShowPlan();
        ShowCurrent();
    }

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

    private void ShowPlan()
    {
        Timeline.ItemsSource = _controller.Plan;
        EmptyText.Visibility = _controller.Plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowCurrent()
    {
        var item = _controller.CurrentItem;
        UpdateButtons();

        NowKind.Content = item;
        NowKind.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        NowTitle.Text = item?.Title ?? "Nothing on air";

        Timeline.SelectedIndex = _controller.CurrentIndex;
        if (Timeline.SelectedItem is { } selected)
            Timeline.ScrollIntoView(selected);

        UpdateProgress();
    }

    private void UpdateButtons()
    {
        var playing = _controller.IsPlaying;
        StartButton.IsEnabled = !playing && _controller.Plan.Count > 0;
        StopButton.IsEnabled = playing;
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
}
