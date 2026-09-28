using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// What P's Aero page renders share (Plan S, P5): a page laid out at a width in a window dressed by
/// <see cref="AeroHost.Dressed"/> (review 11), over a stand-in for the desktop the glass shows in the App (a render has
/// no DWM backdrop), drawn whole to <c>aero-*.png</c>, and a walk for anything cut off.
/// </summary>
internal static class AeroPages
{
    /// <summary>The page widths a 960 and a 1440 px Aero window leave: less the sidebar (240), the gap (18) and the stage's sides (24 each).</summary>
    public const double Narrow = 960 - 240 - 18 - 48;
    public const double Wide = 1440 - 240 - 18 - 48;

    public static readonly Theme[] Themes = [Theme.Dark, Theme.Light];

    /// <summary>A wallpaper-like backdrop for the glass, as the demo's "wallpaper" choice: deep blue and violet glows in the
    /// dark theme, pale ones in the light. Test colours only; the App shows the user's own desktop.</summary>
    public static Brush Backdrop(Theme theme)
    {
        var dark = theme == Theme.Dark;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(dark ? Color.FromRgb(0x0A, 0x0F, 0x1F) : Color.FromRgb(0xE6, 0xE9, 0xF2)), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        void Glow(double x, double y, double r, Color colour)
            => group.Children.Add(new GeometryDrawing(
                new RadialGradientBrush(colour, Color.FromArgb(0, colour.R, colour.G, colour.B)), null, new EllipseGeometry(new Point(x, y), r, r)));
        Glow(0.28, 0.35, 0.45, dark ? Color.FromArgb(0x8C, 0x40, 0x6E, 0xFF) : Color.FromArgb(0x70, 0x9D, 0xB8, 0xFF));
        Glow(0.72, 0.62, 0.4, dark ? Color.FromArgb(0x6B, 0x96, 0x50, 0xFF) : Color.FromArgb(0x60, 0xC9, 0xB5, 0xFF));
        Glow(0.62, 0.12, 0.3, dark ? Color.FromArgb(0x47, 0x00, 0xC8, 0xFF) : Color.FromArgb(0x50, 0x9E, 0xE8, 0xFF));
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    /// <summary>A page laid out at <paramref name="width"/> and its whole length over the backdrop, as the page host shows it.</summary>
    public static PageHost Page(FrameworkElement view, double width, Theme theme)
    {
        view.Width = width;
        var host = new Border { Child = view, Background = Backdrop(theme), Padding = new Thickness(0, 16, 0, 0) };
        var window = AeroHost.Dressed(new Window
        {
            Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        }, theme);
        window.SetResourceReference(TextElement.FontFamilyProperty, "A.F.Ui");
        window.SetResourceReference(TextElement.ForegroundProperty, "A.B.Text");
        window.Show();
        Pump(TimeSpan.FromMilliseconds(700));
        host.UpdateLayout();
        return new PageHost(host, window, theme);
    }

    /// <summary>Each named render, in both themes, holds at least <paramref name="atLeast"/> bytes: a page drawn, not a blank.</summary>
    public static void Sizes(long atLeast, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var theme in Themes)
            {
                new FileInfo(Path.Combine(Folder, $"aero-{name}-{theme}.png")).Length.ShouldBeGreaterThan(atLeast, $"{name} on {theme}");
            }
        }
    }

    /// <summary>
    /// What <paramref name="view"/> cuts off: anything that runs past the side of the page's scroller or past the inside of
    /// its glass panel, and any one-line text wider than the room it was given, which WPF clips without an ellipsis.
    /// </summary>
    public static List<string> CutOff(FrameworkElement view)
    {
        var problems = new List<string>();
        var scroller = Find<ScrollViewer>(view)!;
        foreach (var element in MidnightHost.AllOf<FrameworkElement>((DependencyObject)scroller.Content).Where(e => e.IsVisible && e is TextBlock or ButtonBase or Border))
        {
            if (element.TemplatedParent is TextBox) continue;
            var right = element.TranslatePoint(new Point(element.ActualWidth, 0), scroller).X;
            if (right > scroller.ViewportWidth + 0.5) problems.Add($"{Describe(element)} runs past the side");
            if (Ancestor<GlassPanel>(element) is { } panel
                && right > panel.TranslatePoint(new Point(panel.ActualWidth - panel.Padding.Right, 0), scroller).X + 0.5
                && element.TemplatedParent is null)
            {
                problems.Add($"{Describe(element)} runs past its panel");
            }
            if (element is TextBlock { TextWrapping: TextWrapping.NoWrap, TextTrimming: TextTrimming.None } line && line.Text.Length > 0)
            {
                var room = Math.Min(line.ActualWidth, LayoutInformation.GetLayoutSlot(line).Width - line.Margin.Left - line.Margin.Right);
                if (Written(line) > room + 1) problems.Add($"{Describe(line)} is cut off at {room:0} of {Written(line):0}");
            }
        }
        return problems;
    }

    public static T? Ancestor<T>(DependencyObject element, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T candidate && (match is null || match(candidate))) return candidate;
        }
        return null;
    }

    private static double Written(TextBlock line)
    {
        var face = new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch);
        var text = new FormattedText(line.Text, CultureInfo.CurrentUICulture, line.FlowDirection, face, line.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(line).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace + line.Padding.Left + line.Padding.Right;
    }

    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock text => $"text \"{text.Text}\"",
        ButtonBase button => $"button {button.Content}",
        _ => element.GetType().Name + (element.Name.Length > 0 ? " " + element.Name : ""),
    };

    internal sealed record PageHost(Border Host, Window Window, Theme Theme) : IDisposable
    {
        /// <summary>The whole page to <c>aero-<paramref name="name"/>-{theme}.png</c>, then what it cuts off, of which there is nothing.</summary>
        public void Render(string name)
        {
            Host.UpdateLayout();
            var width = (int)Math.Ceiling(Host.ActualWidth);
            var height = (int)Math.Ceiling(Host.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Host);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Folder);
            using (var file = File.Create(Path.Combine(Folder, $"aero-{name}-{Theme}.png"))) png.Save(file);
            CutOff((FrameworkElement)Host.Child).ShouldBeEmpty($"{name} on {Theme}");
        }

        public void Dispose() => Window.Close();
    }
}
