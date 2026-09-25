using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PowerLedger.App.Tests;

/// <summary>
/// What the Midnight render tests add to <see cref="UiHarness"/>: a Midnight palette on the window a test draws, and a
/// walk over the colours a window paints with. Review 11: the palette goes on the window, never among the application's
/// dictionaries. The UI tests share one dispatcher, and a test that pumps it runs other tests' work inside its pump, so a
/// palette put on the application while one test drew came out in another's renders: Classic PNGs in Midnight's colours.
/// </summary>
internal static class MidnightHost
{
    /// <summary>Midnight's palette for <paramref name="theme"/> on <paramref name="window"/>, where it wins over the
    /// application's for everything the window holds, the Classic keys it also defines among them; the window returned.</summary>
    public static T Dressed<T>(T window, Theme theme)
        where T : Window
    {
        window.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, theme));
        return window;
    }

    /// <summary>Every colour Midnight's palette for <paramref name="theme"/> holds, opaque or not.</summary>
    public static IReadOnlySet<Color> PaletteColours(Theme theme)
        => ThemeManager.Palette(Look.Midnight, theme).Values.OfType<SolidColorBrush>().Select(brush => brush.Color).ToHashSet();

    /// <summary>The colour of the pixel at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="visual"/> drawn at
    /// <paramref name="width"/> × <paramref name="height"/>, for a test to check which palette a render took.</summary>
    public static Color PixelOf(Visual visual, int width, int height, int x, int y)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Every <typeparamref name="T"/> under <paramref name="root"/>, outermost first.</summary>
    public static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in AllOf<T>(child)) yield return deeper;
        }
    }

    /// <summary>
    /// Every solid colour a window's own elements paint with, other than none: text, borders, shapes and the window's
    /// ground. WPF's own chrome is left out, since it is not the App's XAML: an Expander's header toggle, the corner a
    /// ScrollViewer keeps between two bars, and a list's frame and row chrome, which the App draws at no thickness.
    /// </summary>
    public static IEnumerable<(string Where, Color Colour)> PaintedColours(Window window)
    {
        if (window.Background is SolidColorBrush ground) yield return ("the window", ground.Color);
        foreach (var element in AllOf<FrameworkElement>(window))
        {
            if (element.TemplatedParent is System.Windows.Controls.ScrollViewer or System.Windows.Controls.ListBox or System.Windows.Controls.ListBoxItem) continue;
            if (element.TemplatedParent is ToggleButton and not (System.Windows.Controls.CheckBox or System.Windows.Controls.RadioButton)) continue;
            var brushes = element switch
            {
                System.Windows.Controls.TextBlock text => [("text " + Describe(text.Text), text.Foreground)],
                System.Windows.Controls.Border border => new[] { ("a border's ground", border.Background), ("a border's edge", border.BorderBrush) },
                System.Windows.Shapes.Shape shape => [("a shape's fill", shape.Fill), ("a shape's stroke", shape.Stroke)],
                _ => [],
            };
            foreach (var (where, brush) in brushes)
            {
                if (brush is SolidColorBrush { Color.A: > 0 } solid) yield return (where, solid.Color);
            }
        }
    }

    private static string Describe(string text) => text.Length > 30 ? "\"" + text[..30] + "…\"" : "\"" + text + "\"";
}
