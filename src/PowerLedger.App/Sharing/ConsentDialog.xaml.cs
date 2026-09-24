using System.Windows;

namespace PowerLedger.App;

/// <summary>The consent dialog (data-sharing design §2): modal, owned by the main window. Closes itself once the view
/// model says the choice was taken; the X sends nothing, since nothing here reacts to it.</summary>
public partial class ConsentDialog : Window
{
    /// <summary>Room left above and below the dialog on a screen too short for all of it.</summary>
    private const double ScreenMargin = 24;

    internal ConsentDialog(ConsentViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
        // No taller than the screen, so the buttons, in a row of their own, are always in view; the text above them scrolls.
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 2 * ScreenMargin);
        Loaded += (_, _) => KeepOnScreen();
    }

    /// <summary>Centred on its owner, a dialog can start above the screen's top or run past its bottom; this moves it back.</summary>
    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top + ScreenMargin, Math.Min(Top, area.Bottom - ActualHeight - ScreenMargin));
    }
}
