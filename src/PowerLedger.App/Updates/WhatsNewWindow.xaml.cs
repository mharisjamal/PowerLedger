using System.Windows;

namespace PowerLedger.App;

/// <summary>What's new (owner's round): modeless, owned by the main window, single-instance like Add a PC and Send
/// feedback, and fitting a short screen the same way they do.</summary>
public partial class WhatsNewWindow : Window
{
    private const double ScreenMargin = 24;

    internal WhatsNewWindow(WhatsNewViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }
}
