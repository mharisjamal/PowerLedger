using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>The Report screen's layout (spec §9); everything it shows comes from <see cref="ReportViewModel"/>.</summary>
public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    /// <summary>"PNG": the report's sheet as it stands, at the screen's resolution. The view model asks where to save it.</summary>
    private void SavePicture(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportViewModel model || Sheet.ActualWidth < 1 || Sheet.ActualHeight < 1) return;
        var size = new Size(Sheet.ActualWidth, Sheet.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(Sheet);
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("Brush.Panel"), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(Sheet)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size),
            }, null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        model.SaveImage(png.Save);
    }
}
