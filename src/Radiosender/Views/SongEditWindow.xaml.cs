using System.Windows;
using Radiosender.Models;
using Wpf.Ui.Controls;

namespace Radiosender.Views;

public partial class SongEditWindow : FluentWindow
{
    public SongEditWindow(Song song)
    {
        InitializeComponent();
        TitleBox.Text = song.Title;
        ArtistBox.Text = song.Artist;
        LicenseBox.Text = song.License;
        AttributionBox.Text = song.Attribution;
    }

    public string SongTitle => TitleBox.Text;
    public string Artist => ArtistBox.Text;
    public string License => LicenseBox.Text;
    public string Attribution => AttributionBox.Text;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SongTitle) || string.IsNullOrWhiteSpace(Artist))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
