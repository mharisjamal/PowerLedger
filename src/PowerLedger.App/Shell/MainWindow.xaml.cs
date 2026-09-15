using System.Windows;

namespace PowerLedger.App;

/// <summary>The App's window (spec §9). Closing it hides it to the tray; the App decides that in Task 13.</summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
