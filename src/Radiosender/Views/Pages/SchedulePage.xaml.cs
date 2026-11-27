using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Localization;
using Radiosender.Services.Scheduling;
using Radiosender.Services.Streaming;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Radiosender.Views.Pages;

public partial class SchedulePage : Page
{
    private readonly ScheduleController _controller;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;
    private int _scrolledTo = -1;
    private (bool OnAir, bool Live, bool Enabled)? _shownStart;

    private readonly ObservableCollection<TimelineRow> _rows = [];

    private bool _working;

    // Optimistic: shown before engine catches up.
    private bool? _expectPlaying;

    private readonly Microphone _microphone;
    private readonly ProgramMixer _mixer;
    private readonly LiveController _live;

    public SchedulePage(ScheduleController controller, Microphone microphone, ProgramOutput output, LiveController live)
    {
        _controller = controller;
        _live = live;
        _microphone = microphone;
        _mixer = output.Mixer;
        InitializeComponent();
        Timeline.ItemsSource = _rows;
        // Buttons too: Finished can arrive before engine reports stopped.
        _progressTimer.Tick += (_, _) =>
        {
            UpdateButtons();
            UpdateProgress();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _controller.ItemStarted += OnItemStarted;
        _controller.ItemFailed += OnItemFailed;
        _controller.Finished += OnFinished;
        _live.Problem += OnLiveProblem;
        Loc.Changed += OnLanguageChanged;

        _loading = true;
        MusicForMeSlider.Value = _mixer.MusicForMe;
        MusicForOthersSlider.Value = _mixer.MusicForOthers;
        MicrophoneForOthersSlider.Value = _mixer.MicrophoneForOthers;
        ListenToBroadcastBox.IsChecked = _mixer.ListenToBroadcast;
        MusicForMeSlider.IsEnabled = !_mixer.ListenToBroadcast;
        _loading = false;
        ShowMicrophone();
        FillStartTimes();
        if (_controller.Plan.Count > 0)
            UnlimitedBox.IsChecked = _controller.IsUnlimited;

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
        _live.Problem -= OnLiveProblem;
        Loc.Changed -= OnLanguageChanged;
        _progressTimer.Stop();
    }

    // Rewrite every text set in code; the open InfoBar keeps its language.
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _shownStart = null;
        var selected = Timeline.SelectedIndex;
        _rows.Clear();
        NowKind.Content = null; // same item again would not refresh the chip
        ShowMicrophone();
        FillStartTimes();
        ShowCurrent();
        if (selected < _rows.Count)
            Timeline.SelectedIndex = selected;
    });

    // Rebuilt on dropdown open: never offers a past hour.
    private void FillStartTimes()
    {
        var selected = (StartBox.SelectedItem as StartOption)?.Time;
        var now = DateTime.Now;
        var next = now.Date.AddHours(now.Hour + 1);
        List<StartOption> options =
        [
            new(null, Loc.T("Schedule_Now")),
            .. Enumerable.Range(0, 12)
                .Select(i => next.AddHours(i))
                .Select(t => new StartOption(t, t.Date == now.Date ? $"{t:HH:mm}" : t.ToString("ddd HH:mm", Loc.Culture))),
        ];
        StartBox.ItemsSource = options;
        StartBox.SelectedItem = options.FirstOrDefault(o => o.Time == selected) ?? options[0];
    }

    private void OnStartDropDownOpened(object? sender, EventArgs e) => FillStartTimes();

    private void OnUnlimitedChanged(object sender, RoutedEventArgs e) =>
        HoursBox.IsEnabled = UnlimitedBox.IsChecked != true;

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "H.mm", "HH.mm", "H"];

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        int? hours = UnlimitedBox.IsChecked == true ? null : (int)(HoursBox.Value ?? 1);
        if (ChosenStart() is not { } start)
        {
            ShowMessage(InfoBarSeverity.Warning, Loc.T("Schedule_StartUnclearTitle"), Loc.T("Schedule_StartUnclear"));
            return;
        }
        await RunAsync(Loc.T("Schedule_CreateFailed"),() => _controller.CreatePlan(hours, start));
    }

    // Typed time already passed today: tomorrow.
    private DateTime? ChosenStart()
    {
        if (StartBox.SelectedItem is StartOption option)
            return option.Time ?? DateTime.Now;
        if (!TimeOnly.TryParseExact(StartBox.Text.Trim(), TimeFormats, Loc.Culture, System.Globalization.DateTimeStyles.None, out var time))
            return null;
        var start = DateTime.Today.Add(time.ToTimeSpan());
        return start <= DateTime.Now ? start.AddDays(1) : start;
    }

    private void OnStartNowToggle(object sender, RoutedEventArgs e)
    {
        if (OnAir)
            EndBroadcast();
        else
            StartBroadcast();
    }

    private bool OnAir => (_expectPlaying ?? _controller.IsPlaying) || _live.IsLive;

    private async void StartBroadcast()
    {
        _expectPlaying = true;
        if (await RunAsync(Loc.T("Schedule_StartFailed"), _controller.Start) && _live.IsEnabled)
            await GoLiveAsync();
    }

    // Music keeps playing when laut.fm fails: a broken stream must not silence the program.
    private async Task GoLiveAsync()
    {
        _working = true;
        UpdateButtons();
        try
        {
            await Task.Run(() => _live.StartAsync());
        }
        catch (Exception ex) when (ex is LiveStreamException or NAudio.MmException)
        {
            ShowMessage(InfoBarSeverity.Warning, Loc.T("Schedule_NoLautFm"), ex.Message);
        }
        finally
        {
            _working = false;
            UpdateButtons();
        }
    }

    private async void EndBroadcast()
    {
        NowKind.Visibility = Visibility.Collapsed;
        _expectPlaying = false;
        UpdateProgress();
        await RunAsync(Loc.T("Schedule_EndBroadcastFailed"), () =>
        {
            _live.Stop();
            if (_controller.IsPlaying)
                _controller.Stop();
        });
    }

    private async Task<bool> RunAsync(string errorTitle, Action work)
    {
        _working = true;
        UpdateButtons();
        try
        {
            await Task.Run(work);
            Message.IsOpen = false;
            return true;
        }
        catch (Exception ex) when (ex is ScheduleException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            ShowMessage(InfoBarSeverity.Error, errorTitle, ex.Message);
            return false;
        }
        finally
        {
            _working = false;
            _expectPlaying = null;
            ShowCurrent();
        }
    }

    private async void OnSwap(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedItem is not TimelineRow row || RequestSong.SelectedItem is not SongOption song)
            return;
        ScheduleItem? placed = null;
        if (await RunAsync(Loc.T("Schedule_SwapFailed"), () => placed = _controller.Swap(row.Index, song.Id)[row.Index]))
            ShowMessage(InfoBarSeverity.Success, Loc.T("Schedule_SwapDone"),
                Loc.F("Schedule_SwapDoneText", placed!.Title, placed.Start));
    }

    private void OnRequestChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (Timeline.SelectedItem is TimelineRow row)
            MoveRow(row.Index, row.Index + offset);
    }

    private void OnMixChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _mixer.MusicForMe = (float)MusicForMeSlider.Value;
        _mixer.MusicForOthers = (float)MusicForOthersSlider.Value;
        _mixer.MicrophoneForOthers = (float)MicrophoneForOthersSlider.Value;
        _mixer.ListenToBroadcast = ListenToBroadcastBox.IsChecked == true;
        MusicForMeSlider.IsEnabled = !_mixer.ListenToBroadcast;
    }

    private async void OnMicToggle(object sender, RoutedEventArgs e)
    {
        var turnOn = !_microphone.IsOn;
        MicButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                if (turnOn) _microphone.TurnOn();
                else _microphone.TurnOff();
            });
            Message.IsOpen = false;
        }
        catch (InvalidOperationException ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.T("Schedule_MicUnavailable"), ex.Message);
        }
        MicButton.IsEnabled = true;
        ShowMicrophone();
    }

    private void ShowMicrophone()
    {
        var on = _microphone.IsOn;
        MicButton.Content = Loc.T(on ? "Schedule_MicOff" : "Schedule_MicOn");
        MicButton.Icon = new SymbolIcon(on ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
        MicButton.Appearance = on ? ControlAppearance.Danger : ControlAppearance.Secondary;
        System.Windows.Automation.AutomationProperties.SetName(MicButton, Loc.T(on ? "Schedule_MicOffName" : "Schedule_MicOnName"));
    }

    // Background thread.
    private void OnLiveProblem(object? sender, string message) => Dispatcher.BeginInvoke(() =>
    {
        ShowMessage(InfoBarSeverity.Warning, "laut.fm", message);
        UpdateButtons();
    });

    private void ShowStartButton()
    {
        var onAir = OnAir;
        var live = _live.IsLive;
        if (_shownStart == (onAir, live, _live.IsEnabled))
            return;
        _shownStart = (onAir, live, _live.IsEnabled);
        StartNowButton.Content = Loc.T(onAir ? "Schedule_EndBroadcast" : _live.IsEnabled ? "Schedule_GoLive" : "Schedule_StartNow");
        StartNowButton.Icon = new SymbolIcon(onAir ? SymbolRegular.Stop24 : _live.IsEnabled ? SymbolRegular.Live24 : SymbolRegular.Play24);
        StartNowButton.Appearance = onAir ? ControlAppearance.Danger : ControlAppearance.Secondary;
        StartNowButton.ToolTip = Loc.T(onAir
            ? live ? "Schedule_StopLiveTip" : "Schedule_StopTip"
            : _live.IsEnabled
                ? "Schedule_StartLiveTip"
                : "Schedule_StartPlanTip");
        System.Windows.Automation.AutomationProperties.SetName(StartNowButton, (string)StartNowButton.Content);
    }

    private void OnEndNow(object sender, RoutedEventArgs e)
    {
        try
        {
            _controller.EndCurrentNow();
            Message.IsOpen = false;
        }
        catch (ScheduleException ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.T("Schedule_EndItemFailed"), ex.Message);
        }
        ShowCurrent();
    }

    private void OnRemove(object sender, RoutedEventArgs e) => ChangeSelected(row => _controller.Remove(row.Index));

    private void OnAddItem(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ScheduleItemKind kind })
            return;
        var index = InsertPosition();
        if (kind == ScheduleItemKind.Song)
        {
            if (RequestSong.SelectedItem is not SongOption song)
            {
                ShowMessage(InfoBarSeverity.Warning, Loc.T("Schedule_NoSong"), Loc.T("Schedule_NoSongText"));
                return;
            }
            if (ChangePlan(() => _controller.Insert(index, kind, "", TimeSpan.Zero, song.Id), select: index))
                FadeIn(index);
            return;
        }
        var length = kind switch
        {
            ScheduleItemKind.News => TimeSpan.FromMinutes(5),
            ScheduleItemKind.Ad => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromMinutes(1),
        };
        var dialog = new ScheduleItemEditWindow(new ScheduleItem(kind, "", default, length), adding: true) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && ChangePlan(() => _controller.Insert(index, kind, dialog.ItemTitle, dialog.Duration), select: index))
            FadeIn(index);
    }

    // Always the one after next: two after the item on air, else position 2.
    private int InsertPosition() => Math.Min((_controller.IsPlaying ? _controller.CurrentIndex : 0) + 2, _rows.Count);

    private Point _dragStart;
    private TimelineRow? _dragRow;
    private TimelineDragAdorner? _drag;
    private ScrollViewer? _timelineScroll;
    private long _lastScroll;

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Timeline);
        _dragRow = RowAt(e.OriginalSource) is { CanMove: true } row ? row : null;
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is not { } row)
            return;
        var moved = e.GetPosition(Timeline) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragRow = null;
        if (Container(row.Index) is not { } item || AdornerLayer.GetAdornerLayer(Timeline) is not { } layer)
            return;
        var label = row.Item.Title.Length > 0 ? $"{row.Item.KindName} · {row.Item.Title}" : row.Item.KindName;
        _drag = new TimelineDragAdorner(Timeline, label, e.GetPosition(Timeline),
            ThemeBrush("AccentFillColorDefaultBrush", SystemColors.HighlightBrush),
            ThemeBrush("SolidBackgroundFillColorTertiaryBrush", SystemColors.WindowBrush),
            ThemeBrush("TextFillColorPrimaryBrush", SystemColors.WindowTextBrush),
            ThemeBrush("CardStrokeColorDefaultBrush", SystemColors.ActiveBorderBrush));
        layer.Add(_drag);
        item.Opacity = 0.35;
        try
        {
            DragDrop.DoDragDrop(Timeline, row, DragDropEffects.Move);
        }
        finally
        {
            item.Opacity = 1;
            layer.Remove(_drag);
            _drag = null;
        }
    }

    private void OnTimelineDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(TimelineRow)) is not TimelineRow row)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var cursor = e.GetPosition(Timeline);
        var y = cursor.Y;
        var gap = DropGap(y, out var lineY);
        e.Effects = gap >= 0 ? DragDropEffects.Move : DragDropEffects.None;
        // No line where the row would stay put.
        _drag?.Update(cursor, gap >= 0 && gap != row.Index && gap != row.Index + 1 ? lineY : null);
        AutoScroll(y);
    }

    // Also raised when the cursor crosses child elements: hide the line only when really outside.
    private void OnTimelineDragLeave(object sender, DragEventArgs e)
    {
        var at = e.GetPosition(Timeline);
        if (at.X < 0 || at.Y < 0 || at.X >= Timeline.ActualWidth || at.Y >= Timeline.ActualHeight)
            _drag?.Update(at, null);
    }

    private void OnTimelineDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TimelineRow)) is not TimelineRow row)
            return;
        var gap = DropGap(e.GetPosition(Timeline).Y, out _);
        var target = gap > row.Index ? gap - 1 : gap;
        if (gap >= 0 && target != row.Index)
            MoveRow(row.Index, target);
    }

    // Gap under the cursor, 0..Count: before the first row whose middle is below. -1: before the item on air.
    private int DropGap(double y, out double lineY)
    {
        lineY = 0;
        var gap = -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (Container(i) is not { } item)
                continue;
            var top = TopOf(item);
            if (y < top + item.ActualHeight / 2)
            {
                (gap, lineY) = (i, top);
                break;
            }
            (gap, lineY) = (i + 1, top + item.ActualHeight);
        }
        return _controller.IsPlaying && gap <= _controller.CurrentIndex ? -1 : gap;
    }

    // Edges of the rows area, not of the list: the column header sits on top.
    private void AutoScroll(double y)
    {
        const double Edge = 24;
        if (Environment.TickCount64 - _lastScroll < 120)
            return;
        _timelineScroll ??= Descendant<ScrollViewer>(Timeline);
        var header = Descendant<System.Windows.Controls.GridViewHeaderRowPresenter>(Timeline);
        var top = header is null ? 0 : header.TranslatePoint(new Point(0, header.ActualHeight), Timeline).Y;
        if (y >= top && y < top + Edge)
            _timelineScroll?.LineUp();
        else if (y > Timeline.ActualHeight - Edge)
            _timelineScroll?.LineDown();
        else
            return;
        _lastScroll = Environment.TickCount64;
    }

    private void MoveRow(int from, int to)
    {
        if (ChangePlan(() => _controller.Move(from, to - from), select: to))
            FadeIn(to);
    }

    // Fade only, no sliding: rows are rebuilt after a change, so old positions are not reliable.
    private void FadeIn(int index)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        Timeline.UpdateLayout();
        Container(index)?.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private FrameworkElement? Container(int index) =>
        Timeline.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement { IsVisible: true } item ? item : null;

    private double TopOf(UIElement item) => item.TranslatePoint(default, Timeline).Y;

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
                return found;
        }
        return null;
    }

    private TimelineRow? RowAt(object source) =>
        source is DependencyObject element && ItemsControl.ContainerFromElement(Timeline, element) is System.Windows.Controls.ListViewItem item
            ? item.DataContext as TimelineRow
            : null;

    private void OnEditItem(object sender, RoutedEventArgs e)
    {
        if (!EditItemButton.IsEnabled || Timeline.SelectedItem is not TimelineRow row)
            return;
        var dialog = new ScheduleItemEditWindow(row.Item) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            ChangeSelected(r => _controller.EditItem(r.Index, dialog.ItemTitle, dialog.Duration));
    }

    private void ChangeSelected(Action<TimelineRow> change)
    {
        if (Timeline.SelectedItem is TimelineRow row)
            ChangePlan(() => change(row));
    }

    private bool ChangePlan(Action change, int? select = null)
    {
        var changed = true;
        try
        {
            change();
            Message.IsOpen = false;
        }
        catch (ScheduleException ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.T("Schedule_ChangeFailed"), ex.Message);
            changed = false;
        }
        ShowCurrent();
        if (changed && select is { } index)
            Timeline.SelectedIndex = index;
        return changed;
    }

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    // BeginInvoke, not Invoke: engine Stop waits for this pool thread -> deadlock.
    private void OnItemStarted(object? sender, int index) => Dispatcher.BeginInvoke(ShowCurrent);

    private void OnItemFailed(object? sender, string message) =>
        Dispatcher.BeginInvoke(() => ShowMessage(InfoBarSeverity.Warning, Loc.T("Schedule_ItemSkipped"), message));

    private void OnFinished(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ShowMessage(InfoBarSeverity.Informational, Loc.T("Schedule_Finished"), Loc.T("Schedule_FinishedText"));
        ShowCurrent();
    });

    private void ShowCurrent()
    {
        var item = _controller.CurrentItem;
        var current = _controller.CurrentIndex;
        var plan = _controller.Plan;

        var selected = Timeline.SelectedIndex;
        // Replace changed rows only: keeps selection (swap target).
        for (var i = 0; i < plan.Count; i++)
        {
            var updated = new TimelineRow(i, plan[i], i == current, !_controller.IsPlaying || i > current);
            if (i == _rows.Count)
                _rows.Add(updated);
            else if (_rows[i] != updated)
                _rows[i] = updated;
        }
        while (_rows.Count > plan.Count)
            _rows.RemoveAt(_rows.Count - 1);
        GridColumns.Fit(Timeline);
        Timeline.SelectedIndex = selected < _rows.Count ? selected : -1;
        if (current != _scrolledTo && current >= 0 && current < _rows.Count)
            Timeline.ScrollIntoView(_rows[current]);
        else if (Timeline.SelectedItem is { } row)
            Timeline.ScrollIntoView(row);
        _scrolledTo = current;
        EmptyText.Visibility = plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        NowKind.Content = item;
        NowKind.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is not null) NowTitle.Text = item.Title.Length > 0 ? item.Title : item.KindName;

        UpdateButtons();
        UpdateProgress();
    }

    private void UpdateButtons()
    {
        var playing = _expectPlaying ?? _controller.IsPlaying;
        CreateButton.IsEnabled = !_working;
        // Without a plan only stopping makes sense.
        StartNowButton.IsEnabled = !_working && (OnAir || _controller.Plan.Count > 0);
        ShowStartButton();

        var row = Timeline.SelectedItem as TimelineRow;

        var count = _controller.Plan.Count;
        var current = _controller.CurrentIndex;
        bool Editable(int i) => !_working && i >= 0 && i < count && (!playing || i > current);
        var index = row?.Index ?? -1;
        AddButton.IsEnabled = !_working && count > 0 && (!playing || index >= current || index < 0);
        MoveUpButton.IsEnabled = Editable(index) && Editable(index - 1);
        MoveDownButton.IsEnabled = Editable(index) && Editable(index + 1);
        RemoveButton.IsEnabled = Editable(index);
        var onAir = !_working && playing && index >= 0 && index == current;
        EditItemButton.IsEnabled = (Editable(index) || onAir) && row!.Item.Kind != ScheduleItemKind.Song;
        EndNowButton.IsEnabled = !_working && _expectPlaying != false && _controller.CurrentItem is not null;

        var blocker = row switch
        {
            null => Loc.T("Schedule_HintSelect"),
            { Item.Kind: ScheduleItemKind.News } => Loc.T("Schedule_NewsNoReplace"),
            _ when playing && row.Index <= _controller.CurrentIndex => Loc.T("Schedule_HintOnAirOrOver"),
            _ => null,
        };
        SwapHint.Text = blocker ?? Loc.F("Schedule_HintReplaces", row!.Item.Start);
        var requested = RequestSong.SelectedItem is not null;
        SwapHint.Visibility = requested ? Visibility.Visible : Visibility.Collapsed;
        SwapButton.IsEnabled = !_working && requested && blocker is null;
    }

    private void UpdateProgress()
    {
        if (_expectPlaying == false || _controller.CurrentItem is not { } item)
        {
            NowProgress.Value = 0;
            // Here, not in ShowCurrent: engine starts waiting shortly after Start.
            var waiting = _expectPlaying == false ? null : _controller.WaitingUntil;
            NowTitle.Text = waiting is { } at ? Loc.F("Schedule_WaitingUntil", at) : Loc.T("Schedule_NothingOnAir");
            NowTime.Text = " ";
            return;
        }

        var elapsed = _controller.CurrentElapsed;
        NowProgress.Value = item.Duration.Ticks == 0 ? 0 : (double)elapsed.Ticks / item.Duration.Ticks;
        NowTime.Text = $"{elapsed:m\\:ss} / {item.Duration:m\\:ss}";
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

    private sealed record TimelineRow(int Index, ScheduleItem Item, bool IsCurrent, bool CanMove);

    private sealed record SongOption(long Id, string Label);

    private sealed record StartOption(DateTime? Time, string Label); // Time null: now
}
