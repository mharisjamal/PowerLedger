using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PowerLedger.App;

/// <summary>
/// The frosted backdrop under a tooltip (plan O 0.5): when the panel loads it takes one picture of the window beneath it,
/// blurs that, and lies it under the glass fill. The blur runs on a snapshot the size of the tooltip, never on the window's
/// live tree, so an opening costs a few kilobytes of pixels and nothing while it stays open (CLAUDE.md: BlurEffect on small
/// surfaces only). A panel with no window under it, or one not yet on screen, simply shows the fill.
/// </summary>
internal static class Glass
{
    public static readonly DependencyProperty BackdropProperty = DependencyProperty.RegisterAttached(
        "Backdrop", typeof(bool), typeof(Glass), new PropertyMetadata(false, OnBackdropChanged));

    public static readonly DependencyProperty BlurProperty = DependencyProperty.RegisterAttached(
        "Blur", typeof(double), typeof(Glass), new PropertyMetadata(12.0));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Glass), new PropertyMetadata(8.0));

    public static bool GetBackdrop(DependencyObject element) => (bool)element.GetValue(BackdropProperty);

    public static void SetBackdrop(DependencyObject element, bool value) => element.SetValue(BackdropProperty, value);

    public static double GetBlur(DependencyObject element) => (double)element.GetValue(BlurProperty);

    public static void SetBlur(DependencyObject element, double value) => element.SetValue(BlurProperty, value);

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnBackdropChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Panel panel) return;
        if (e.NewValue is true)
        {
            panel.Loaded += Frost;
            panel.Unloaded += Clear;
        }
        else
        {
            panel.Loaded -= Frost;
            panel.Unloaded -= Clear;
        }
    }

    private static void Frost(object sender, RoutedEventArgs e)
    {
        var panel = (Panel)sender;
        panel.Children.Clear();
        if (Snapshot(panel) is not { } picture) return;
        var radius = GetRadius(panel);
        panel.Clip = new RectangleGeometry(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight), radius, radius);
        panel.Children.Add(new Rectangle
        {
            Fill = new ImageBrush(picture) { Stretch = Stretch.Fill },
            Effect = new BlurEffect { Radius = GetBlur(panel), KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        });
    }

    /// <summary>The picture goes with the popup, so a tooltip that is never shown again holds no pixels.</summary>
    private static void Clear(object sender, RoutedEventArgs e)
    {
        var panel = (Panel)sender;
        panel.Children.Clear();
        panel.Clip = null;
    }

    /// <summary>The part of the window under <paramref name="panel"/>, at the window's pixel scale; null when there is none to take.</summary>
    private static RenderTargetBitmap? Snapshot(FrameworkElement panel)
    {
        if (Under(panel) is not { } content || !(panel.ActualWidth > 0) || !(panel.ActualHeight > 0) || PresentationSource.FromVisual(panel) is null) return null;
        Rect region;
        try
        {
            var topLeft = content.PointFromScreen(panel.PointToScreen(new Point(0, 0)));
            var bottomRight = content.PointFromScreen(panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight)));
            region = new Rect(topLeft, bottomRight);
        }
        catch (InvalidOperationException)
        {
            return null;   // the window is not on screen
        }
        var dpi = VisualTreeHelper.GetDpi(content);
        var brush = new VisualBrush(content)
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, region.Width, region.Height));
        var picture = new RenderTargetBitmap(
            (int)Math.Ceiling(region.Width * dpi.DpiScaleX), (int)Math.Ceiling(region.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        picture.Render(visual);
        picture.Freeze();
        return picture;
    }

    /// <summary>The content of the window a popup floats over, found through the popup's placement target.</summary>
    private static FrameworkElement? Under(DependencyObject panel)
    {
        for (var node = panel; node is not null; node = Parent(node))
        {
            var target = node switch
            {
                ToolTip tip => tip.PlacementTarget,
                ContextMenu menu => menu.PlacementTarget,
                Popup popup => popup.PlacementTarget,
                _ => null,
            };
            if (target is not null) return Window.GetWindow(target)?.Content as FrameworkElement;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject node)
        => (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);
}
