using System.Windows;
using Radiosender.Services.Localization;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace Radiosender.Views;

// Errors as a modal popup, so they cannot be missed like an InfoBar at the top of a long page.
public static class ErrorPopup
{
    public static void Show(DependencyObject source, string title, string message) =>
        _ = new MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = Loc.T("Main_Ok"),
            Owner = Window.GetWindow(source) is { IsLoaded: true } owner ? owner : null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        }.ShowDialogAsync();
}
