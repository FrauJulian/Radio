using System.Windows;
using Microsoft.Win32;
using Radiosender.Controllers;
using Radiosender.Services;
using Wpf.Ui.Controls;

namespace Radiosender.Views.Pages;

public partial class UploadPage
{
    private readonly UploadController _controller =
        new(new SongImportService(App.Songs, SongImportService.DefaultMusicDirectory));

    public UploadPage()
    {
        InitializeComponent();
    }

    private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = UploadController.FileDialogFilter };
        if (dialog.ShowDialog() != true)
            return;

        var result = _controller.Upload(dialog.FileName);
        new Snackbar(SnackbarPresenter)
        {
            Title = result.Success ? "Song uploaded" : "Upload failed",
            Content = result.Message,
            Appearance = result.Success ? ControlAppearance.Success : ControlAppearance.Danger,
            Icon = new SymbolIcon(result.Success ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.ErrorCircle24),
            Timeout = TimeSpan.FromSeconds(5),
        }.Show();
    }
}
