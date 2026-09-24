using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Midnight;

/// <summary>Midnight's Report page (Plan O task M2-2): the export row and the sheet as a grid of cards, over <see cref="ReportViewModel"/>.</summary>
public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    /// <summary>"PNG": the sheet with the ground around it, as the page shows it.</summary>
    private void SavePicture(object sender, RoutedEventArgs e)
    {
        if (DataContext is ReportViewModel model) SheetPicture.Save(Sheet, (Brush)FindResource("M.Ground"), model);
    }
}
