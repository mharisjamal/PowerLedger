using System.Windows.Controls;

namespace PowerLedger.App.Aero;

/// <summary>Aero's History page (Aero look design §1; Plan S, P1): the range as sliding pills, the parts stacked in the
/// chart well, and each part's energy and share with a search over the rows and Save CSV, over <see cref="BreakdownViewModel"/>.
/// The custom days are left to Classic's and Midnight's History: the range pills cover the rest.</summary>
public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
}
