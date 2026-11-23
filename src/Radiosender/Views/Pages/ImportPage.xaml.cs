using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.Wave;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

/// <summary>Adds music to the library from local files or from Openverse; both list their results in one place.</summary>
public partial class ImportPage : Page
{
    // One Openverse controller (and HttpClient) for the app lifetime; created on first use, after App.Songs exists.
    private static OpenverseController? _openverse;
    private static OpenverseController Openverse => _openverse ??= new(OpenverseClient.CreateDefault(), App.Songs);

    private readonly UploadController _upload =
        new(new SongImportService(App.Songs, SongImportService.DefaultMusicDirectory));

    private readonly ObservableCollection<SessionRow> _session = [];
    // Preview has its own output, so it never interrupts a running broadcast.
    private WaveOut? _previewOutput;
    private MediaFoundationReader? _previewReader;
    private int _previewId; // changes on every start and stop; a late callback with an old id is ignored
    private bool _uploading;
    private bool _busy;
    private bool _previewing;

    public ImportPage()
    {
        InitializeComponent();
        Session.ItemsSource = _session;
        _session.CollectionChanged += (_, _) =>
        {
            SessionPanel.Visibility = _session.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            GridColumns.Fit(Session);
        };
    }

    // ---- From computer ----

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

    // Files are imported one by one on a pool thread; each result shows up as soon as it is known.
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

    // ---- Openverse ----

    private ResultRow? Selected => Results.SelectedItem as ResultRow;

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

        SetBusy($"Suche auf Openverse nach \"{query}\"...");
        Message.IsOpen = false;
        try
        {
            var tracks = await Openverse.SearchAsync(query);
            var imported = Openverse.ImportedIds();
            Results.ItemsSource = tracks.Select(t => ResultRow.For(t, imported)).ToList();
            GridColumns.Fit(Results);
            if (tracks.Count == 0)
                Show(InfoBarSeverity.Informational, "Keine Ergebnisse", $"Openverse hat nichts zu \"{query}\" gefunden.");
        }
        catch (OpenverseException ex)
        {
            Show(InfoBarSeverity.Error, "Suche fehlgeschlagen", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    // One button: starts the preview, or stops the running one.
    // NAudio, not WPF's MediaPlayer: that one cannot open https URLs.
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
            // Connects to the server, so it runs off the UI thread. The file is streamed, nothing is saved.
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
        // Disposing waits for the audio thread, which can sit in a network read; not on the UI thread.
        if (output is not null || reader is not null)
            Task.Run(() =>
            {
                output?.Dispose();
                reader?.Dispose();
            });
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => OnImport(sender, e);

    // Everything selected that can still be imported.
    private List<ResultRow> Importable => Results.SelectedItems.OfType<ResultRow>().Where(r => r.CanImport).ToList();

    // One after the other: Openverse limits anonymous requests, and each result shows up as soon as it is done.
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
                    var song = await Openverse.ImportAsync(row.Track);
                    _session.Insert(0, SessionRow.Added(song, song.License ?? ""));
                    ReplaceRow(row, row with { Status = "In Bibliothek", CanImport = false });
                }
                catch (Exception ex) when (ex is OpenverseException or Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    // The database error text is technical and English; the usual cause is a song that is already stored.
                    var reason = ex is OpenverseException ? ex.Message : "Der Song konnte nicht in der Bibliothek gespeichert werden.";
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
        // Why the selection cannot be imported ("In Bibliothek", "Keine Dauer"); shown on the disabled button.
        ImportButton.ToolTip = importable == 0 && Selected is { } blocked ? blocked.Status : null;
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }

    // ---- Shared ----

    private void OnOpenLibrary(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.RootNavigation.Navigate(typeof(LibraryPage));

    private void OnUnloaded(object sender, RoutedEventArgs e) => OnStop(sender, e);

    private sealed record ResultRow(OpenverseTrack Track, string Status, bool CanImport)
    {
        public static ResultRow For(OpenverseTrack track, IReadOnlySet<string> imported) =>
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
