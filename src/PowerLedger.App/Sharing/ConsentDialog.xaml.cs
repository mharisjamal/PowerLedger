using System.Windows;

namespace PowerLedger.App;

/// <summary>The consent dialog (data-sharing design §2): modal, owned by the main window. Closes itself once the view
/// model says the choice was taken; the X sends nothing, since nothing here reacts to it.</summary>
public partial class ConsentDialog : Window
{
    internal ConsentDialog(ConsentViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Closed += Close;
    }
}
