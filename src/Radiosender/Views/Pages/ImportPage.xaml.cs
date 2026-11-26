using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.Wave;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Localization;
using Radiosender.Services.Providers;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Radiosender.Views.Pages;

public partial class ImportPage : Page
{
    private readonly OnlineImportController _online;
    private readonly UploadController _upload;

    private readonly ObservableCollection<SessionRow> _session = [];
    private readonly ObservableCollection<ResultRow> _results = [];
    // Own output: never interrupts broadcast.
    private WaveOut? _previewOutput;
    private MediaFoundationReader? _previewReader;
    private int _previewId; // bumped on start/stop; late callbacks with old id ignored
    private bool _uploading;
    private bool _busy;
    private bool _previewing;

    public ImportPage(OnlineImportController online, UploadController upload)
    {
        _online = online;
        _upload = upload;
        InitializeComponent();
        OnlineTab.Visibility = _online.Providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.ItemsSource = _results;
        foreach (var license in LicenseType.All)
        {
            var item = new MenuItem { Header = license.Label, Tag = license.Code, IsCheckable = true, IsChecked = true, StaysOpenOnClick = true };
            item.Click += (_, _) => ShowLicenseCount();
            LicenseMenu.Items.Add(item);
        }
        ShowLicenseCount();
        Session.ItemsSource = _session;
        _session.CollectionChanged += (_, _) =>
        {
            SessionPanel.Visibility = _session.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            GridColumns.Fit(Session);
        };
    }

    private async void OnChooseFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = UploadController.FileDialogFilter, Multiselect = true };
        if (dialog.ShowDialog() == true)
            await UploadAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = !_uploading && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, ok ? "AccentTextFillColorPrimaryBrush" : "ControlStrongStrokeColorDefaultBrush");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "ControlStrongStrokeColorDefaultBrush");

    private async void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (!_uploading && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await UploadAsync(paths);
    }

    private async Task UploadAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _uploading = true;
        ChooseFilesButton.IsEnabled = false;
        UploadProgressPanel.Visibility = Visibility.Visible;
        UploadProgress.Maximum = paths.Count;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                UploadProgress.Value = i;
                UploadProgressText.Text = Loc.F("Import_Uploading", i + 1, paths.Count, Path.GetFileName(path));
                var result = await Task.Run(() => _upload.Upload(path));
                _session.Insert(0, result.Song is { } song
                    ? SessionRow.Added(song, Path.GetFileName(path))
                    : SessionRow.Failed(Path.GetFileName(path), result.Message));
            }
        }
        finally
        {
            _uploading = false;
            ChooseFilesButton.IsEnabled = true;
            UploadProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private ResultRow? Selected => Results.SelectedItem as ResultRow;

    private List<MenuItem> LicenseItems => [.. LicenseMenu.Items.OfType<MenuItem>()];

    // null when all are checked: providers then also keep licenses outside LicenseType.All.
    private IReadOnlySet<string>? CheckedLicenses()
    {
        var items = LicenseItems;
        return items.All(item => item.IsChecked)
            ? null
            : items.Where(item => item.IsChecked).Select(item => (string)item.Tag).ToHashSet();
    }

    private void ShowLicenseCount()
    {
        var items = LicenseItems;
        var count = items.Count(item => item.IsChecked);
        LicenseButton.Content = count == items.Count ? Loc.T("Import_AllLicenses")
            : count == 1 ? items.First(item => item.IsChecked).Header
            : Loc.F("Import_LicenseCount", count);
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnSearch(sender, e);
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0 || _busy)
            return;
        var licenses = CheckedLicenses();
        if (licenses is { Count: 0 })
        {
            Show(InfoBarSeverity.Warning, Loc.T("Import_NoLicense"), Loc.T("Import_NoLicenseHint"));
            return;
        }

        var total = _online.Providers.Count;
        var done = 0;
        List<string> errors = [];
        SetBusy(Loc.F("Import_Searching", query));
        Message.IsOpen = false;
        _results.Clear();
        try
        {
            var imported = _online.ImportedIds();
            await foreach (var result in _online.SearchAllAsync(query, licenses))
            {
                foreach (var track in result.Tracks)
                    _results.Add(ResultRow.For(track, imported, result.Provider.Name));
                if (result.Error is { } error)
                    errors.Add(error.Message);
                GridColumns.Fit(Results);
                if (++done < total)
                    SetBusy(Loc.F("Import_SearchingProgress", query, done, total));
            }
        }
        finally
        {
            SetBusy(null);
        }

        if (errors.Count > 0)
            Show(_results.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                Loc.T(errors.Count == total ? "Import_SearchFailed" : "Import_SearchPartial"), string.Join(" ", errors));
        else if (_results.Count == 0)
            Show(InfoBarSeverity.Informational, Loc.T("Import_NoResults"),
                licenses is null
                    ? Loc.F("Import_NothingFound", query)
                    : Loc.F("Import_NothingFoundLicenses", query));
    }

    // NAudio, not WPF MediaPlayer: MediaPlayer cannot open https.
    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_previewing)
        {
            OnStop(sender, e);
            return;
        }
        if (Selected is not { } row || !Uri.TryCreate(row.Track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        SetPreviewing(true);
        Message.IsOpen = false;
        var id = ++_previewId;
        try
        {
            var reader = await Task.Run(() => new MediaFoundationReader(uri.AbsoluteUri));
            if (id != _previewId) // stopped or page left in the meantime
            {
                reader.Dispose();
                return;
            }
            _previewReader = reader;
            _previewOutput = new WaveOut();
            _previewOutput.PlaybackStopped += (_, stopped) => Dispatcher.BeginInvoke(() =>
            {
                if (id != _previewId) return;
                OnStop(this, new RoutedEventArgs());
                if (stopped.Exception is { } error)
                    Show(InfoBarSeverity.Error, Loc.T("Import_PreviewFailed"), error.Message);
            });
            _previewOutput.Init(reader);
            _previewOutput.Play();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (id != _previewId) return;
            OnStop(this, new RoutedEventArgs());
            Show(InfoBarSeverity.Error, Loc.T("Import_PreviewFailed"), ex.Message);
        }
    }

    private void SetPreviewing(bool on)
    {
        _previewing = on;
        PreviewButton.Content = Loc.T(on ? "Import_Stop" : "Import_Preview");
        PreviewButton.Icon = new SymbolIcon(on ? SymbolRegular.Stop24 : SymbolRegular.Play24);
        UpdateButtons();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _previewId++;
        var output = _previewOutput;
        var reader = _previewReader;
        _previewOutput = null;
        _previewReader = null;
        SetPreviewing(false);
        // Off UI thread: Dispose waits for audio thread, may block in network read.
        if (output is not null || reader is not null)
            Task.Run(() =>
            {
                output?.Dispose();
                reader?.Dispose();
            });
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => OnImport(sender, e);

    private List<ResultRow> Importable => [.. Results.SelectedItems.OfType<ResultRow>().Where(r => r.CanImport)];

    // Sequential: providers rate-limit anonymous requests.
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var rows = Importable;
        if (rows.Count == 0 || _busy)
            return;

        Message.IsOpen = false;
        var failed = 0;
        var lastError = "";
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                SetBusy(rows.Count == 1
                    ? Loc.F("Import_Downloading", row.Track.Title)
                    : Loc.F("Import_DownloadingMany", i + 1, rows.Count, row.Track.Title));
                try
                {
                    var song = await _online.ImportAsync(row.Track);
                    _session.Insert(0, SessionRow.Added(song, song.License ?? ""));
                    ReplaceRow(row, row with { Status = "Import_StatusInLibrary", CanImport = false });
                }
                catch (Exception ex) when (ex is MusicProviderException or Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    // DB error text is technical English; usual cause: song already stored.
                    var reason = ex is MusicProviderException ? ex.Message : Loc.T("Import_SaveFailedSong");
                    _session.Insert(0, SessionRow.Failed(row.Track.Title, reason));
                    failed++;
                    lastError = reason;
                }
            }
        }
        finally
        {
            SetBusy(null);
        }

        if (failed > 0)
            Show(InfoBarSeverity.Error, Loc.T("Import_Failed"), rows.Count == 1
                ? lastError
                : Loc.F("Import_FailedMany", failed, rows.Count));
    }

    private void ReplaceRow(ResultRow old, ResultRow updated)
    {
        if (_results.IndexOf(old) is var i and >= 0)
            _results[i] = updated;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void SetBusy(string? text)
    {
        _busy = text is not null;
        Busy.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? "";
        SearchButton.IsEnabled = !_busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        PreviewButton.IsEnabled = _previewing || Selected is not null;
        var importable = Importable.Count;
        ImportButton.IsEnabled = !_busy && importable > 0;
        ImportButton.Content = importable > 1 ? Loc.F("Import_ImportCount", importable) : Loc.T("Import_ImportButton");
        ImportButton.ToolTip = importable == 0 && Selected is { } blocked ? Loc.T(blocked.Status) : null;
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        if (severity == InfoBarSeverity.Error)
        {
            Message.IsOpen = false;
            ErrorPopup.Show(this, title, message);
            return;
        }
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }

    private void OnOpenLibrary(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.RootNavigation.Navigate(typeof(LibraryPage));

    private void OnLoaded(object sender, RoutedEventArgs e) => Loc.Changed += OnLanguageChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loc.Changed -= OnLanguageChanged;
        OnStop(sender, e);
    }

    // Texts set in code and values computed into rows.
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ShowLicenseCount();
        SetPreviewing(_previewing);
        Results.Items.Refresh();
        GridColumns.Fit(Results);
    }

    private sealed record ResultRow(MusicTrack Track, string Source, string Status, bool CanImport)
    {
        public static ResultRow For(MusicTrack track, IReadOnlySet<string> imported, string source) =>
            imported.Contains(track.Id) ? new(track, source, "Import_StatusInLibrary", false)
            : !track.CanImport ? new(track, source, "Import_StatusNoDuration", false)
            : new(track, source, "", true);
    }

    private sealed record SessionRow(bool Success, string Title, string Artist, string Details)
    {
        public static SessionRow Added(Song song, string details) => new(true, song.Title, song.Artist, details);

        public static SessionRow Failed(string name, string error) => new(false, name, "", error);
    }
}
