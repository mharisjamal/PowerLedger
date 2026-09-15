using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>The Report screen's layout (spec §9); everything it shows comes from <see cref="ReportViewModel"/>.</summary>
public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    /// <summary>"PNG": the report's sheet as it stands, with a margin, at the screen's resolution. The view model asks where to save it.</summary>
    private void SavePicture(object sender, RoutedEventArgs e)
    {
        const double margin = 24;
        if (DataContext is not ReportViewModel model || Sheet.ActualWidth < 1 || Sheet.ActualHeight < 1) return;
        var sheet = new Rect(margin, margin, Sheet.ActualWidth, Sheet.ActualHeight);
        var page = new Rect(0, 0, sheet.Width + 2 * margin, sheet.Height + 2 * margin);
        var dpi = VisualTreeHelper.GetDpi(Sheet);
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("Brush.Panel"), null, page);
            // The brush maps the sheet's own bounds, which its background fills, so its place on screen doesn't shift it.
            dc.DrawRectangle(new VisualBrush(Sheet), null, sheet);
        }
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(page.Width * dpi.DpiScaleX), (int)Math.Ceiling(page.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        model.SaveImage(png.Save);
    }
}
