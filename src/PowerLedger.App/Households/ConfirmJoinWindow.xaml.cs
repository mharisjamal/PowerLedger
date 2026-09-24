using System.Windows;

namespace PowerLedger.App;

/// <summary>The Confirm join prompt (households design §7, task 0.8): modal on top of the main window, closing itself
/// once answered or once its notice's time runs out.</summary>
public partial class ConfirmJoinWindow : Window
{
    /// <summary>Room left above and below the dialog on a screen too short for all of it.</summary>
    private const double ScreenMargin = 24;

    internal ConfirmJoinWindow(ConfirmJoinViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }
}
