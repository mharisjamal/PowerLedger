using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>Aero's Reports page (Aero look design §1; Plan S, P3): the range and the exports, then the sheet as glass
/// panels of figures in wells, as the demo's report dialog lists them, over <see cref="ReportViewModel"/>.</summary>
public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    /// <summary>"PNG": the sheet on the palette's ground, as the other looks save it; the glass keeps its tint over it.</summary>
    private void SavePicture(object sender, RoutedEventArgs e)
    {
        if (DataContext is ReportViewModel model) SheetPicture.Save(Sheet, (Brush)FindResource("Brush.Ground"), model);
    }
}
