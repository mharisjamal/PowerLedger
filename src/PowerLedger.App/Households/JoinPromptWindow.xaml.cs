using System.Windows;

namespace PowerLedger.App;

/// <summary>The Join prompt (households design §2, §3): modal on top of the main window, closing itself once answered or
/// once its notice's time runs out.</summary>
public partial class JoinPromptWindow : Window
{
    /// <summary>Room left above and below the dialog on a screen too short for all of it.</summary>
    private const double ScreenMargin = 24;

    internal JoinPromptWindow(JoinPromptViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
        // Service round, review: however this window actually closes — answered, timed out, or force-closed for a
        // Withdraw — the model's own timer is stopped for good, so it can never fire late.
        Closed += (_, _) => model.Stop();
        MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }
}
