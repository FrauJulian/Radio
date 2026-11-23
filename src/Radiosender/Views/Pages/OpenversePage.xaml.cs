using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class OpenversePage : Page
{
    // One controller (and HttpClient) for the app lifetime; created on first use, after App.Songs exists.
    private static OpenverseController? _controller;
    private static OpenverseController Controller => _controller ??= new(OpenverseClient.CreateDefault(), App.Songs);

    private readonly MediaPlayer _preview = new();
    private bool _busy;

    public OpenversePage()
    {
        InitializeComponent();
        _preview.MediaEnded += (_, _) => OnStop(this, new RoutedEventArgs());
        _preview.MediaFailed += (_, e) =>
        {
            OnStop(this, new RoutedEventArgs());
            Show(InfoBarSeverity.Error, "Preview failed", e.ErrorException.Message);
        };
    }

    private OpenverseTrack? Selected => Results.SelectedItem as OpenverseTrack;

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

        SetBusy(true);
        Message.IsOpen = false;
        try
        {
            var tracks = await Controller.SearchAsync(query);
            Results.ItemsSource = tracks;
            if (tracks.Count == 0)
                Show(InfoBarSeverity.Informational, "No results", $"Openverse found no audio for \"{query}\".");
        }
        catch (OpenverseException ex)
        {
            Show(InfoBarSeverity.Error, "Search failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } track || !Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
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

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (Selected is not { CanImport: true } track || _busy)
            return;

        SetBusy(true);
        Message.IsOpen = false;
        try
        {
            var song = await Controller.ImportAsync(track);
            Show(InfoBarSeverity.Success, "Imported", $"\"{song.Title}\" was added to the library.");
        }
        catch (Exception ex) when (ex is OpenverseException or Microsoft.Data.Sqlite.SqliteException)
        {
            Show(InfoBarSeverity.Error, "Import failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnUnloaded(object sender, RoutedEventArgs e) => OnStop(sender, e);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SearchButton.IsEnabled = !busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        PreviewButton.IsEnabled = Selected is not null;
        ImportButton.IsEnabled = !_busy && Selected is { CanImport: true };
        ImportHint.Visibility = Selected is { CanImport: false } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = message;
        Message.IsOpen = true;
    }
}
