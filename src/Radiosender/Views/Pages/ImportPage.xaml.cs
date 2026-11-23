using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
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
    private readonly MediaPlayer _preview = new();
    private bool _uploading;
    private bool _busy;

    public ImportPage()
    {
        InitializeComponent();
        Session.ItemsSource = _session;
        _session.CollectionChanged += (_, _) =>
            SessionEmpty.Visibility = _session.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _preview.MediaEnded += (_, _) => OnStop(this, new RoutedEventArgs());
        _preview.MediaFailed += (_, e) =>
        {
            OnStop(this, new RoutedEventArgs());
            Show(InfoBarSeverity.Error, "Preview failed", e.ErrorException.Message);
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
        DropBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, ok ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropBorder.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorTertiaryBrush");

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
                UploadProgressText.Text = $"Importing {i + 1} of {paths.Count}: {Path.GetFileName(path)}";
                var result = await Task.Run(() => _upload.Upload(path));
                _session.Insert(0, result.Song is { } song
                    ? SessionRow.Added(song, "Upload", Path.GetFileName(path))
                    : SessionRow.Failed(Path.GetFileName(path), "Upload", result.Message));
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

        SetBusy($"Searching Openverse for \"{query}\"...");
        Message.IsOpen = false;
        try
        {
            var tracks = await Openverse.SearchAsync(query);
            var imported = Openverse.ImportedIds();
            Results.ItemsSource = tracks.Select(t => ResultRow.For(t, imported)).ToList();
            if (tracks.Count == 0)
                Show(InfoBarSeverity.Informational, "No results", $"Openverse found no audio for \"{query}\".");
        }
        catch (OpenverseException ex)
        {
            Show(InfoBarSeverity.Error, "Search failed", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || !Uri.TryCreate(row.Track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        _preview.Open(uri); // streams the file, nothing is saved
        _preview.Play();
        StopButton.IsEnabled = true;
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _preview.Stop();
        _preview.Close();
        StopButton.IsEnabled = false;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => OnImport(sender, e);

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (Selected is not { CanImport: true } row || _busy)
            return;

        SetBusy($"Downloading \"{row.Track.Title}\"...");
        Message.IsOpen = false;
        try
        {
            var song = await Openverse.ImportAsync(row.Track);
            _session.Insert(0, SessionRow.Added(song, "Openverse", song.License ?? ""));
            ReplaceRow(row, row with { Status = "In library", CanImport = false });
        }
        catch (Exception ex) when (ex is OpenverseException or Microsoft.Data.Sqlite.SqliteException)
        {
            _session.Insert(0, SessionRow.Failed(row.Track.Title, "Openverse", ex.Message));
            Show(InfoBarSeverity.Error, "Import failed", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private void ReplaceRow(ResultRow old, ResultRow updated)
    {
        if (Results.ItemsSource is not List<ResultRow> rows || rows.IndexOf(old) is var i && i < 0) return;
        rows[i] = updated;
        Results.Items.Refresh();
        Results.SelectedIndex = i;
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
        PreviewButton.IsEnabled = Selected is not null;
        ImportButton.IsEnabled = !_busy && Selected is { CanImport: true };
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
            imported.Contains(track.Id) ? new(track, "In library", false)
            : !track.CanImport ? new(track, "No duration", false)
            : new(track, "", true);
    }

    private sealed record SessionRow(bool Success, string Title, string Artist, string Duration, string Source, string Details)
    {
        public static SessionRow Added(Song song, string source, string details) =>
            new(true, song.Title, song.Artist, $"{(int)song.Duration.TotalMinutes}:{song.Duration.Seconds:00}", source, details);

        public static SessionRow Failed(string name, string source, string error) => new(false, name, "", "", source, error);
    }
}
