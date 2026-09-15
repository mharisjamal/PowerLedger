using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The first-run wizard's layout (spec §9); everything it shows comes from <see cref="WizardViewModel"/>.</summary>
public partial class WizardView : UserControl
{
    public WizardView() => InitializeComponent();
}
