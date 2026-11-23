using Radiosender.Views.Pages;
using Wpf.Ui.Controls;

namespace Radiosender.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RootNavigation.Navigate(typeof(HomePage));
    }
}
