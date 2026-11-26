using System.Windows;
using System.Windows.Controls;
using Radiosender.Controllers;
using Radiosender.Models;
using Radiosender.Services.Localization;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace Radiosender.Views.Pages;

public partial class LibraryPage : Page
{
    private readonly LibraryController _controller;

    public LibraryPage(LibraryController controller)
    {
        _controller = controller;
        InitializeComponent();
    }

    private Song? Selected => SongList.SelectedItem as Song;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.Changed += OnLanguageChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Loc.Changed -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) => ShowEmptyText();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        EditButton.IsEnabled = DeleteButton.IsEnabled = Selected is not null;

    private void Refresh()
    {
        var songs = _controller.Search(SearchBox.Text);
        SongList.ItemsSource = songs;
        GridColumns.Fit(SongList);
        EmptyText.Visibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowEmptyText();
    }

    private void ShowEmptyText() =>
        EmptyText.Text = Loc.T(string.IsNullOrWhiteSpace(SearchBox.Text) ? "Library_Empty" : "Library_NoMatch");

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
            Title = Loc.T("Library_DeleteTitle"),
            Content = Loc.F("Library_DeleteConfirm", song.Title, song.Artist),
            PrimaryButtonText = Loc.T("Library_Delete"),
            CloseButtonText = Loc.T("Library_Cancel"),
        }.ShowDialogAsync();

        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        _controller.Delete(song.Id);
        Refresh();
    }
}
