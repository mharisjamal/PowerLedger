using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PowerLedger.App.Tests;

/// <summary>
/// The Midnight render tests' harness. The process holds one WPF application, which <see cref="RenderingTests"/> starts
/// on its own thread; this reaches that thread through reflection until Plan O task F2 lifts the harness into a shared
/// UiHarness, when everything here but <see cref="UsePalette"/> goes. A Midnight palette is put last among the merged
/// dictionaries, where WPF looks first, so the Classic keys it also defines win while it is in and go with it.
/// </summary>
internal static class MidnightHost
{
    public static readonly string Folder = RenderingTests.Folder;

    private static readonly MethodInfo OnUiMethod = typeof(RenderingTests).GetMethod("OnUi", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Runs <paramref name="work"/> on the application's thread, and throws here what it threw there.</summary>
    public static void OnUi(Action work)
    {
        try
        {
            OnUiMethod.Invoke(null, [work]);
        }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
        }
    }

    /// <summary>Midnight's palette for <paramref name="theme"/> over the application's dictionaries until disposed. Call on the UI thread.</summary>
    public static IDisposable UsePalette(Theme theme)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var palette = MidnightPalettes.Of(theme);
        merged.Add(palette);
        return new Removal(merged, palette);
    }

    /// <summary>A dictionary among the application's until disposed, after the palette. Call on the UI thread.</summary>
    public static IDisposable Use(ResourceDictionary dictionary)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Add(dictionary);
        return new Removal(merged, dictionary);
    }

    public static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void Save(Visual visual, int width, int height, string name)
    {
        Directory.CreateDirectory(Folder);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder, name));
        png.Save(file);
    }

    /// <summary>The first <typeparamref name="T"/> under <paramref name="root"/>, outermost first, that <paramref name="match"/> accepts.</summary>
    public static T? Find<T>(DependencyObject root, Func<T, bool>? match = null)
        where T : DependencyObject
        => AllOf<T>(root).FirstOrDefault(found => match is null || match(found));

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
