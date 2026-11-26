using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.Wave;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Providers;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class ImportPage : Page
{
    private readonly OnlineImportController _online;
    private readonly UploadController _upload;

    private readonly ObservableCollection<SessionRow> _session = [];
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
        var providers = _online.Providers;
        ProviderBox.ItemsSource = providers;
        ProviderBox.SelectedIndex = providers.Count > 0 ? 0 : -1;
        ProviderBox.Visibility = providers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        OnlineTab.Visibility = providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (providers.Count == 1)
            OnlineTab.Header = providers[0].Name;
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
                UploadProgressText.Text = $"Importiere {i + 1} von {paths.Count}: {Path.GetFileName(path)}";
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

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnSearch(sender, e);
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0 || _busy || ProviderBox.SelectedItem is not IMusicProvider provider)
            return;

        SetBusy($"Suche auf {provider.Name} nach \"{query}\"...");
        Message.IsOpen = false;
        try
        {
            var tracks = await provider.SearchAsync(query);
            var imported = _online.ImportedIds();
            Results.ItemsSource = tracks.Select(t => ResultRow.For(t, imported)).ToList();
            GridColumns.Fit(Results);
            if (tracks.Count == 0)
                Show(InfoBarSeverity.Informational, "Keine Ergebnisse", $"{provider.Name} hat nichts zu \"{query}\" gefunden.");
        }
        catch (MusicProviderException ex)
        {
            Show(InfoBarSeverity.Error, "Suche fehlgeschlagen", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
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
                    Show(InfoBarSeverity.Error, "Vorhören fehlgeschlagen", error.Message);
            });
            _previewOutput.Init(reader);
            _previewOutput.Play();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (id != _previewId) return;
            OnStop(this, new RoutedEventArgs());
            Show(InfoBarSeverity.Error, "Vorhören fehlgeschlagen", ex.Message);
        }
    }

    private void SetPreviewing(bool on)
    {
        _previewing = on;
        PreviewButton.Content = on ? "Stopp" : "Vorhören";
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
                    ? $"Lade \"{row.Track.Title}\" herunter..."
                    : $"Lade {i + 1} von {rows.Count} herunter: \"{row.Track.Title}\"...");
                try
                {
                    var song = await _online.ImportAsync(row.Track);
                    _session.Insert(0, SessionRow.Added(song, song.License ?? ""));
                    ReplaceRow(row, row with { Status = "In Bibliothek", CanImport = false });
                }
                catch (Exception ex) when (ex is MusicProviderException or Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    // DB error text is technical English; usual cause: song already stored.
                    var reason = ex is MusicProviderException ? ex.Message : "Der Song konnte nicht in der Bibliothek gespeichert werden.";
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
            Show(InfoBarSeverity.Error, "Import fehlgeschlagen", rows.Count == 1
                ? lastError
                : $"{failed} von {rows.Count} Songs konnten nicht importiert werden. Die Gründe stehen in der Liste \"Importiert\".");
    }

    private void ReplaceRow(ResultRow old, ResultRow updated)
    {
        if (Results.ItemsSource is not List<ResultRow> rows || rows.IndexOf(old) is var i && i < 0) return;
        rows[i] = updated;
        Results.Items.Refresh();
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
        ImportButton.Content = importable > 1 ? $"{importable} in Bibliothek importieren" : "In Bibliothek importieren";
        ImportButton.ToolTip = importable == 0 && Selected is { } blocked ? blocked.Status : null;
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }

    private void OnOpenLibrary(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.RootNavigation.Navigate(typeof(LibraryPage));

    private void OnUnloaded(object sender, RoutedEventArgs e) => OnStop(sender, e);

    private sealed record ResultRow(MusicTrack Track, string Status, bool CanImport)
    {
        public static ResultRow For(MusicTrack track, IReadOnlySet<string> imported) =>
            imported.Contains(track.Id) ? new(track, "In Bibliothek", false)
            : !track.CanImport ? new(track, "Keine Dauer", false)
            : new(track, "", true);
    }

    private sealed record SessionRow(bool Success, string Title, string Artist, string Details)
    {
        public static SessionRow Added(Song song, string details) => new(true, song.Title, song.Artist, details);

        public static SessionRow Failed(string name, string error) => new(false, name, "", error);
    }
}
