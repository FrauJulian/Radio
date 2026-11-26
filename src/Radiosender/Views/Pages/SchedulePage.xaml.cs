using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Audio;
using Radiosender.Services.Scheduling;
using Radiosender.Services.Streaming;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class SchedulePage : Page
{
    private readonly ScheduleController _controller;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;
    private int _scrolledTo = -1;
    private bool _shownPlaying;

    // UI language, not Windows culture.
    private static readonly System.Globalization.CultureInfo German = new("de-DE");

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

        _loading = true;
        VolumeSlider.Value = _controller.Volume;
        MusicForMeSlider.Value = _mixer.MusicForMe;
        MusicForOthersSlider.Value = _mixer.MusicForOthers;
        MicrophoneForOthersSlider.Value = _mixer.MicrophoneForOthers;
        ListenToBroadcastBox.IsChecked = _mixer.ListenToBroadcast;
        MusicForMeSlider.IsEnabled = !_mixer.ListenToBroadcast;
        _loading = false;
        ShowMicrophone();
        ShowLive();
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
        _progressTimer.Stop();
    }

    // Rebuilt on dropdown open: never offers a past hour.
    private void FillStartTimes()
    {
        var selected = (StartBox.SelectedItem as StartOption)?.Time;
        var now = DateTime.Now;
        var next = now.Date.AddHours(now.Hour + 1);
        var options = Enumerable.Range(0, 12)
            .Select(i => next.AddHours(i))
            .Select(t => new StartOption(t, t.Date == now.Date ? $"{t:HH:mm}" : t.ToString("ddd HH:mm", German)))
            .ToList();
        StartBox.ItemsSource = options;
        StartBox.SelectedItem = options.FirstOrDefault(o => o.Time == selected) ?? options[0];
    }

    private void OnStartDropDownOpened(object? sender, EventArgs e) => FillStartTimes();

    private void OnUnlimitedChanged(object sender, RoutedEventArgs e) =>
        HoursBox.IsEnabled = UnlimitedBox.IsChecked != true;

    private void OnCreate(object sender, RoutedEventArgs e) => CreatePlan(startNow: false);

    private void OnStartNow(object sender, RoutedEventArgs e) => CreatePlan(startNow: true);

    private async void CreatePlan(bool startNow)
    {
        int? hours = UnlimitedBox.IsChecked == true ? null : (int)(HoursBox.Value ?? 1);
        var start = (StartBox.SelectedItem as StartOption)?.Time;
        if (startNow)
            _expectPlaying = true;
        await RunAsync("Der Plan konnte nicht erstellt werden", () =>
        {
            if (startNow)
                _controller.StartNow(hours);
            else
                _controller.CreatePlan(hours, start);
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
        if (await RunAsync("Der Songwunsch konnte nicht eingeplant werden", () => placed = _controller.Swap(row.Index, song.Id)[row.Index]))
            ShowMessage(InfoBarSeverity.Success, "Songwunsch eingeplant",
                $"\"{placed!.Title}\" läuft um {placed.Start:HH:mm:ss}.");
    }

    private void OnRequestChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset) => ChangeSelected(row =>
    {
        _controller.Move(row.Index, offset);
        Timeline.SelectedIndex = row.Index + offset;
    });

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
            ShowMessage(InfoBarSeverity.Error, "Mikrofon nicht verfügbar", ex.Message);
        }
        MicButton.IsEnabled = true;
        ShowMicrophone();
    }

    private void ShowMicrophone()
    {
        var on = _microphone.IsOn;
        MicButton.Content = on ? "Mikro aus" : "Mikro ein";
        MicButton.Icon = new SymbolIcon(on ? SymbolRegular.MicOff24 : SymbolRegular.Mic24);
        MicButton.Appearance = on ? ControlAppearance.Danger : ControlAppearance.Secondary;
        System.Windows.Automation.AutomationProperties.SetName(MicButton, on ? "Mikrofon ausschalten, es ist auf Sendung" : "Mikrofon einschalten");
    }

    private async void OnLiveToggle(object sender, RoutedEventArgs e)
    {
        LiveButton.IsEnabled = false;
        try
        {
            if (_live.IsLive)
            {
                await Task.Run(_live.Stop);
            }
            else
            {
                await Task.Run(() => _live.StartAsync());
                Message.IsOpen = false;
            }
        }
        catch (Exception ex) when (ex is LiveStreamException or NAudio.MmException)
        {
            ShowMessage(InfoBarSeverity.Error, "Live nicht möglich", ex.Message);
        }
        LiveButton.IsEnabled = true;
        ShowLive();
    }

    // Background thread.
    private void OnLiveProblem(object? sender, string message) => Dispatcher.BeginInvoke(() =>
    {
        ShowMessage(InfoBarSeverity.Warning, "laut.fm", message);
        ShowLive();
    });

    private void ShowLive()
    {
        var live = _live.IsLive;
        // Visible while live: stoppable after laut.fm was switched off.
        LiveButton.Visibility = _live.IsEnabled || live ? Visibility.Visible : Visibility.Collapsed;
        LiveButton.Content = live ? "Live beenden" : "Live";
        LiveButton.Appearance = live ? ControlAppearance.Danger : ControlAppearance.Secondary;
        System.Windows.Automation.AutomationProperties.SetName(LiveButton, live ? "Livestream zu laut.fm beenden, er läuft" : "Livestream zu laut.fm starten");
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
            ShowMessage(InfoBarSeverity.Error, "Das Element konnte nicht beendet werden", ex.Message);
        }
        ShowCurrent();
    }

    private void OnRemove(object sender, RoutedEventArgs e) => ChangeSelected(row => _controller.Remove(row.Index));

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
        if (Timeline.SelectedItem is not TimelineRow row)
            return;
        try
        {
            change(row);
            Message.IsOpen = false;
        }
        catch (ScheduleException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Der Plan konnte nicht geändert werden", ex.Message);
        }
        ShowCurrent();
    }

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void OnPlayToggle(object sender, RoutedEventArgs e)
    {
        var stop = _controller.IsPlaying;
        _expectPlaying = !stop;
        if (stop)
            NowKind.Visibility = Visibility.Collapsed;
        UpdateProgress();
        await RunAsync("Start nicht möglich", stop ? _controller.Stop : _controller.Start);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading) _controller.Volume = (float)e.NewValue;
    }

    // BeginInvoke, not Invoke: engine Stop waits for this pool thread -> deadlock.
    private void OnItemStarted(object? sender, int index) => Dispatcher.BeginInvoke(ShowCurrent);

    private void OnItemFailed(object? sender, string message) =>
        Dispatcher.BeginInvoke(() => ShowMessage(InfoBarSeverity.Warning, "Element übersprungen", message));

    private void OnFinished(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ShowMessage(InfoBarSeverity.Informational, "Sendung beendet", "Der ganze Plan wurde gespielt.");
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
            var updated = new TimelineRow(i, plan[i], i == current);
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
        PlayButton.IsEnabled = !_working && (playing || _controller.Plan.Count > 0);
        CreateButton.IsEnabled = StartNowButton.IsEnabled = !_working;
        if (playing != _shownPlaying)
        {
            _shownPlaying = playing;
            PlayButton.Content = playing ? "Stopp" : "Start";
            PlayButton.Icon = new SymbolIcon(playing ? SymbolRegular.Stop24 : SymbolRegular.Play24);
        }

        var row = Timeline.SelectedItem as TimelineRow;

        var count = _controller.Plan.Count;
        var current = _controller.CurrentIndex;
        bool Editable(int i) => !_working && i >= 0 && i < count && (!playing || i > current);
        var index = row?.Index ?? -1;
        MoveUpButton.IsEnabled = Editable(index) && Editable(index - 1);
        MoveDownButton.IsEnabled = Editable(index) && Editable(index + 1);
        RemoveButton.IsEnabled = Editable(index);
        var onAir = !_working && playing && index >= 0 && index == current;
        EditItemButton.IsEnabled = (Editable(index) || onAir) && row!.Item.Kind != ScheduleItemKind.Song;
        EndNowButton.IsEnabled = !_working && _expectPlaying != false && _controller.CurrentItem is not null;

        var blocker = row switch
        {
            null => "Ein kommendes Element im Plan auswählen.",
            { Item.Kind: ScheduleItemKind.News } => "Nachrichten können nicht ersetzt werden.",
            _ when playing && row.Index <= _controller.CurrentIndex => "Dieses Element ist bereits auf Sendung oder vorbei.",
            _ => null,
        };
        SwapHint.Text = blocker ?? $"Ersetzt das Element um {row!.Item.Start:HH:mm:ss}.";
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
            NowTitle.Text = waiting is { } at ? $"Auf Sendung um {at:HH:mm:ss}" : "Nichts auf Sendung";
            NowTime.Text = " ";
            return;
        }

        var elapsed = _controller.CurrentElapsed;
        NowProgress.Value = item.Duration.Ticks == 0 ? 0 : (double)elapsed.Ticks / item.Duration.Ticks;
        NowTime.Text = $"{elapsed:m\\:ss} / {item.Duration:m\\:ss}";
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

    private sealed record StartOption(DateTime Time, string Label);
}
