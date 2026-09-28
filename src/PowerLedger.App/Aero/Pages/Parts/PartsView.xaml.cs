using System.Windows.Controls;

namespace PowerLedger.App.Aero;

/// <summary>Aero's Parts page (Aero look design §1; Plan S, P2): each part's model, watts now, energy, share, quality and
/// 7-day trend, over the Dashboard's <see cref="DashboardViewModel.Parts"/>, which reads while this page shows.</summary>
public partial class PartsView : UserControl
{
    public PartsView() => InitializeComponent();
}
