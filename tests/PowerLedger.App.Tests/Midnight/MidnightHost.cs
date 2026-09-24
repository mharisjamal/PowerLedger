using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PowerLedger.App.Tests;

/// <summary>
/// What the Midnight render tests add to <see cref="UiHarness"/>: a Midnight palette, or a dictionary, put last among
/// the application's merged dictionaries, where WPF looks first, so the Classic keys a Midnight palette also defines win
/// while it is in and go with it; and a walk over the colours a window paints with.
/// </summary>
internal static class MidnightHost
{
    /// <summary>Midnight's palette for <paramref name="theme"/> over the application's dictionaries until disposed. Call on the UI thread.</summary>
    public static IDisposable UsePalette(Theme theme) => Use(ThemeManager.Palette(Look.Midnight, theme));

    /// <summary>Every colour Midnight's palette for <paramref name="theme"/> holds, opaque or not.</summary>
    public static IReadOnlySet<Color> PaletteColours(Theme theme)
        => ThemeManager.Palette(Look.Midnight, theme).Values.OfType<SolidColorBrush>().Select(brush => brush.Color).ToHashSet();

    /// <summary>A dictionary among the application's until disposed, after the palette. Call on the UI thread.</summary>
    public static IDisposable Use(ResourceDictionary dictionary)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Add(dictionary);
        return new Removal(merged, dictionary);
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

    private sealed class Removal(ICollection<ResourceDictionary> merged, ResourceDictionary dictionary) : IDisposable
    {
        public void Dispose() => merged.Remove(dictionary);
    }
}
