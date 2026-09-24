using System.Windows;

namespace PowerLedger.App;

/// <summary>The recovery code, shown once (households design §7), modal and owned by the main window.</summary>
public partial class RecoveryCodeWindow : Window
{
    internal RecoveryCodeWindow(RecoveryCodeViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }
}
