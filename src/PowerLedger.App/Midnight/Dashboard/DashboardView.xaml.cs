using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PowerLedger.App;

/// <summary>Midnight's landing page (Midnight look design §1), over <see cref="DashboardViewModel"/>.</summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>The period button opens its menu under itself, by the pointer or by Enter or Space; the menu gives the
    /// focus back to the button when it shuts.</summary>
    private void OpenPeriodMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>The menu opens on the period chosen, so the arrows start from it and Enter keeps it.</summary>
    private void FocusChosenPeriod(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var chosen = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsChecked) ?? menu.Items.OfType<MenuItem>().FirstOrDefault();
        chosen?.Dispatcher.BeginInvoke(() => Keyboard.Focus(chosen), System.Windows.Threading.DispatcherPriority.Input);
    }
}
