using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The Report screen's layout (spec §9); everything it shows comes from <see cref="ReportViewModel"/>.</summary>
public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    /// <summary>"PNG": the report's sheet as it stands, with a margin, at the screen's resolution. The view model asks where to save it.</summary>
    private void SavePicture(object sender, RoutedEventArgs e)
    {
        if (DataContext is ReportViewModel model) SheetPicture.Save(Sheet, (Brush)FindResource("Brush.Panel"), model);
    }
}
