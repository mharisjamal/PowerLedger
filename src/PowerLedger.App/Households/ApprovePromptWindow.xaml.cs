using System.Windows;

namespace PowerLedger.App;

/// <summary>The Approve prompt (households design §7): modal on top of the main window, closing itself once answered or
/// once its notice's time runs out.</summary>
public partial class ApprovePromptWindow : Window
{
    /// <summary>Room left above and below the dialog on a screen too short for all of it.</summary>
    private const double ScreenMargin = 24;

    internal ApprovePromptWindow(ApprovePromptViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
        // Service round, review: however this window actually closes — answered, timed out, replaced by a newer prompt
        // for the same request, or force-closed for a Withdraw — the model's own timer is stopped for good.
        Closed += (_, _) => model.Stop();
        MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }
}
