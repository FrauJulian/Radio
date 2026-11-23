using System.Windows;
using System.Windows.Controls;
using Radiosender.Controllers;
using Radiosender.Models;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace Radiosender.Views.Pages;

public partial class LibraryPage : Page
{
    private readonly LibraryController _controller = new(App.Songs);

    public LibraryPage() => InitializeComponent();

    private Song? Selected => SongList.SelectedItem as Song;

    // Loaded fires on every navigation to the page, so the list is reloaded from the database each time.
    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        EditButton.IsEnabled = DeleteButton.IsEnabled = Selected is not null;

    private void Refresh()
    {
        var songs = _controller.Search(SearchBox.Text);
        SongList.ItemsSource = songs;
        GridColumns.Fit(SongList);
        EmptyText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "Noch keine Songs. Musik auf der Seite Import hinzufügen."
            : "Kein Song passt zur Suche.";
        EmptyText.Visibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnEdit(sender, e);
        else if (e.Key == System.Windows.Input.Key.Delete) OnDelete(sender, e);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } song)
            return;

        var dialog = new SongEditWindow(song) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            _controller.Update(song.Id, dialog.SongTitle, dialog.Artist, dialog.License, dialog.Attribution);
        Refresh();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } song)
            return;

        var result = await new MessageBox
        {
            Title = "Song löschen",
            Content = $"\"{song.Title}\" von {song.Artist} aus der Bibliothek löschen?",
            PrimaryButtonText = "Löschen",
            CloseButtonText = "Abbrechen",
        }.ShowDialogAsync();

        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        _controller.Delete(song.Id);
        Refresh();
    }
}
