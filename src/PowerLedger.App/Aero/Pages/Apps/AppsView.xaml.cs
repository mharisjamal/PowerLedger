using System.Windows.Controls;

namespace PowerLedger.App.Aero;

/// <summary>Aero's Apps page (Plan AE): energy and cost by app over Today, this week or this month, a line opening in
/// place to its details, over <see cref="AppsViewModel"/>, which reads while this page shows.</summary>
public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();
}
