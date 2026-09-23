using System.Windows;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>"What's been sent" (data-sharing design §2): every upload kept on this PC, opened through
/// <see cref="PayloadWindow"/> on selection.</summary>
public partial class SentWindow : Window
{
    internal SentWindow(SentViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void RowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SentViewModel model || sender is not ListBox list) return;
        if (list.SelectedItem is SentRow row) model.Open(row);
        list.SelectedItem = null;
    }
}
