using System.Windows;

namespace PowerLedger.App;

/// <summary>The recovery code, shown once (households design §7), modal and owned by the main window.</summary>
public partial class RecoveryCodeWindow : Window
{
    /// <summary>Room left above and below the dialog on a screen too short for all of it.</summary>
    private const double ScreenMargin = 24;

    internal RecoveryCodeWindow(RecoveryCodeViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
        // Review finding A6: OK closes the window, above, but so can the X button or Alt+F4 — either way this answers
        // the prompt, once, so the service can stop holding the code for this PC.
        Closed += (_, _) => model.Answer();
        MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }
}
