using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>"PNG" on either look's Report page: the report's sheet as it stands, with a margin, at the screen's resolution.
/// The view model asks where to save it.</summary>
internal static class SheetPicture
{
    private const double Margin = 24;

    /// <summary>Draws <paramref name="sheet"/> on <paramref name="page"/>, the colour around it, into the file <paramref name="model"/> chooses.</summary>
    public static void Save(FrameworkElement sheet, Brush page, ReportViewModel model)
    {
        if (sheet.ActualWidth < 1 || sheet.ActualHeight < 1) return;
        var inner = new Rect(Margin, Margin, sheet.ActualWidth, sheet.ActualHeight);
        var outer = new Rect(0, 0, inner.Width + 2 * Margin, inner.Height + 2 * Margin);
        var dpi = VisualTreeHelper.GetDpi(sheet);
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawRectangle(page, null, outer);
            // The brush maps the sheet's own bounds, which its background fills, so its place on screen doesn't shift it.
            dc.DrawRectangle(new VisualBrush(sheet), null, inner);
        }
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(outer.Width * dpi.DpiScaleX), (int)Math.Ceiling(outer.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        model.SaveImage(png.Save);
    }
}
