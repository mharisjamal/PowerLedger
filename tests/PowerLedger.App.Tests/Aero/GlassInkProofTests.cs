using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AD's ink over the harsh desktop: the mockup's own (its glows on deep navy, the white Photos window open on it),
/// which the glass bends into white and navy blotches across the panes. The Dashboard in Aero's window, and a ledger like
/// the Apps page's (a period switch, column heads, rows of names, figures and share bars), each word's and icon's ink
/// measured against the glass right under it: its least contrast over every pixel of its bounds, drawn without it. The
/// worst element is reported. Renders go to %TEMP%\powerledger-renders.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class GlassInkProofTests(ITestOutputHelper output)
{
    private const int W = 1440, H = 900;

    /// <summary>The mockup's desktop (Main.dc.html, as Plan AE's renders draw it): its four glows on deep navy, and the
    /// white Photos window open on it.</summary>
    internal static BitmapSource HarshDesktop()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x16, 0x40)), null, new Rect(0, 0, W, H));
            void Glow(double left, double top, double w, double h, double cx, double cy, (Color Colour, double At)[] stops)
            {
                var brush = new RadialGradientBrush { GradientOrigin = new Point(cx, cy), Center = new Point(cx, cy), RadiusX = .5, RadiusY = .5 };
                foreach (var (colour, at) in stops) brush.GradientStops.Add(new GradientStop(colour, at));
                dc.DrawEllipse(brush, null, new Point(left + w / 2, top + h / 2), w / 2, h / 2);
            }
            Glow(-200, -240, 1100, 1000, .55, .55, [(Color.FromRgb(0x3B, 0x7B, 0xFF), 0), (Color.FromRgb(0x1F, 0x49, 0xE0), .36), (Color.FromArgb(0, 0x0F, 0x23, 0x8C), .7)]);
            Glow(640, 220, 1100, 1100, .4, .4, [(Color.FromRgb(0xB0, 0x4D, 0xFF), 0), (Color.FromRgb(0x6D, 0x2D, 0xE0), .34), (Color.FromArgb(0, 0x28, 0x0A, 0x78), .68)]);
            Glow(960, -300, 800, 800, .5, .5, [(Color.FromRgb(0x19, 0xE0, 0xC6), 0), (Color.FromRgb(0x0C, 0x97, 0xB0), .4), (Color.FromArgb(0, 0x05, 0x3C, 0x5A), .7)]);
            Glow(120, 560, 700, 560, .5, .5, [(Color.FromRgb(0xFF, 0x8A, 0x4C), 0), (Color.FromRgb(0xEA, 0x3C, 0x72), .42), (Color.FromArgb(0, 0x5A, 0x0A, 0x3C), .72)]);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xFB)), null, new Rect(560, 120, 640, 470), 16, 16);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xF1)), null, new Rect(560, 120, 640, 40));
        }
        var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record Inked(FrameworkElement Element, string Name, Color Ink, Rect Box);

    /// <summary>Every word and icon shown on glass under <paramref name="root"/>: its ink (its brush at its opacity) and the
    /// bounds of its ink in the window.</summary>
    private static List<Inked> Inks(Window window, DependencyObject root)
    {
        var found = new List<Inked>();
        void Walk(DependencyObject node, bool onGlass)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is UIElement { IsVisible: false }) continue;
                var glass = onGlass || child is GlassPanel;
                if (glass && child is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } element)
                {
                    var (name, brush) = element switch
                    {
                        TextBlock { Text.Length: > 0 } text => ($"'{text.Text}'", text.Foreground),
                        Icon icon when icon.Data != null => ($"icon in {Named(icon)}", icon.Foreground),
                        _ => (null, null),
                    };
                    if (name != null && brush is SolidColorBrush solid)
                    {
                        var opacity = brush.Opacity;
                        for (DependencyObject? n = element; n != null && n != window; n = VisualTreeHelper.GetParent(n))
                            if (n is UIElement u) opacity *= u.Opacity;
                        // Where its ink lies, as the ink itself takes it: a line of text only where its words are.
                        var box = element.TransformToAncestor(window).TransformBounds(GlassInk.InkBox(element));
                        box.Intersect(new Rect(0, 0, W, H));
                        if (opacity > 0.05 && !box.IsEmpty && box.Width >= 2 && box.Height >= 2)
                            found.Add(new Inked(element, name, Color.FromArgb((byte)Math.Round(solid.Color.A * Math.Min(1, opacity)), solid.Color.R, solid.Color.G, solid.Color.B), box));
                    }
                }
                Walk(child, glass);
            }
        }
        Walk(root, root is GlassPanel);
        return found;
    }

    /// <summary>The nearest named element above <paramref name="element"/>, or a question mark.</summary>
    private static string Named(DependencyObject element)
    {
        for (var n = VisualTreeHelper.GetParent(element); n != null; n = VisualTreeHelper.GetParent(n))
            if (n is FrameworkElement { Name.Length: > 0 } named) return named.Name;
        return "?";
    }

    private static byte[] Pixels(BitmapSource ground, Window window, out BitmapSource bitmap)
    {
        bitmap = LiquidGlassProofTests.Over(ground, window, W, H);
        var pixels = new byte[W * H * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, W * 4, 0);
        return pixels;
    }

    private sealed record Measured(Inked Inked, double Least, double Median, double Reads, double OtherReads, double Low, double OtherLow, bool OnDisc);

    /// <summary>
    /// Each element measured against the glass right under it (its bounds drawn without it): its ink laid over each pixel,
    /// against that pixel. Its least contrast and its median; the share of its pixels it reads on at 3:1, and the share the
    /// other ink (dark for white, white for dark, at the same strength) would; and the worst each ink makes but for a stray
    /// pixel (the twentieth from the bottom). Worst least first.
    /// </summary>
    private List<Measured> Measure(BitmapSource ground, Window window, List<Inked> inks, string name)
    {
        Pixels(ground, window, out var bitmap);
        LiquidGlassProofTests.Write(bitmap, System.IO.Path.Combine(Folder, name));
        var opacities = inks.Select(i => i.Element.Opacity).ToList();
        foreach (var inked in inks) inked.Element.Opacity = 0;
        var without = Pixels(ground, window, out var bare);
        LiquidGlassProofTests.Write(bare, System.IO.Path.Combine(Folder, System.IO.Path.ChangeExtension(name, null) + "-noink.png"));
        for (var k = 0; k < inks.Count; k++) inks[k].Element.Opacity = opacities[k];
        var results = new List<Measured>();
        foreach (var inked in inks)
        {
            var light = Contrast.Luminance(Color.FromRgb(inked.Ink.R, inked.Ink.G, inked.Ink.B)) > 0.5;
            var other = light ? Color.FromArgb(inked.Ink.A, 0x16, 0x18, 0x1C) : Color.FromArgb(inked.Ink.A, 255, 255, 255);
            var contrasts = new List<double>();
            var others = new List<double>();
            for (var y = (int)inked.Box.Top; y < (int)Math.Ceiling(inked.Box.Bottom); y++)
            {
                for (var x = (int)inked.Box.Left; x < (int)Math.Ceiling(inked.Box.Right); x++)
                {
                    var at = (y * W + x) * 4;
                    var under = Color.FromRgb(without[at + 2], without[at + 1], without[at]);
                    contrasts.Add(Contrast.Ratio(Contrast.Over(inked.Ink, under), under));
                    others.Add(Contrast.Ratio(Contrast.Over(other, under), under));
                }
            }
            contrasts.Sort();
            others.Sort();
            // Letters on a disc (a PC's initials): the disc is their ground, not the glass; the mockup's pairing.
            var onDisc = VisualTreeHelper.GetParent(inked.Element) is Panel parent && parent.Children.OfType<Ellipse>().Any(e =>
                e.IsVisible && e.TransformToAncestor(window).TransformBounds(new Rect(e.RenderSize)).Contains(new Point(inked.Box.X + inked.Box.Width / 2, inked.Box.Y + inked.Box.Height / 2)));
            results.Add(new Measured(inked, contrasts[0], contrasts[contrasts.Count / 2], contrasts.Count(c => c >= 3) / (double)contrasts.Count,
                others.Count(c => c >= 3) / (double)others.Count, contrasts[contrasts.Count / 20], others[others.Count / 20], onDisc));
        }
        return [.. results.OrderBy(r => r.Least)];
    }

    private void Report(string what, List<Measured> measured)
    {
        var glass = measured.Where(m => !m.OnDisc).ToList();
        var worst = glass[0];
        output.WriteLine($"{what}: {glass.Count} words and icons on glass; worst {worst.Inked.Name}: least {worst.Least:0.00}:1, median {worst.Median:0.00}:1, "
            + $"reads at 3:1 over {worst.Reads:P0} of it (the other ink {worst.OtherReads:P0})");
        foreach (var m in glass.Take(8))
            output.WriteLine($"   {m.Inked.Name} at {m.Inked.Box.X:0},{m.Inked.Box.Y:0}: least {m.Least:0.00}:1, median {m.Median:0.00}:1, reads {m.Reads:P0} (other {m.OtherReads:P0}), ink {m.Inked.Ink}");
        var byMedian = glass.OrderBy(m => m.Median).First();
        output.WriteLine($"   worst median: {byMedian.Inked.Name} {byMedian.Median:0.00}:1; least under 3:1: {glass.Count(m => m.Least < 3)}, median under 3:1: {glass.Count(m => m.Median < 3)}, "
            + $"median under 4.5:1: {glass.Count(m => m.Median < 4.5)}");
        foreach (var m in measured.Where(m => m.OnDisc))
            output.WriteLine($"   on a disc (not the glass): {m.Inked.Name} {m.Median:0.00}:1");
    }

    /// <summary>
    /// Every word and icon on glass took the better ink, by the ink's own rule measured on the pixels: where either ink's
    /// worst (but for a stray pixel) holds 2:1, the one whose worst is the greater, within the margin; where neither does,
    /// the one that reads over more of the glass under it, give or take a tenth.
    /// </summary>
    private static void EachTookTheBetterInk(List<Measured> measured)
    {
        foreach (var m in measured.Where(m => !m.OnDisc))
        {
            var what = $"{m.Inked.Name} at {m.Inked.Box.X:0},{m.Inked.Box.Y:0}: worst {m.Low:0.00}:1 against the other ink's {m.OtherLow:0.00}:1, reads {m.Reads:P0} against {m.OtherReads:P0}";
            if (Math.Max(m.Low, m.OtherLow) >= GlassInk.Hold) m.Low.ShouldBeGreaterThanOrEqualTo(m.OtherLow / GlassInk.Margin, what);
            else m.Reads.ShouldBeGreaterThanOrEqualTo(m.OtherReads - 0.1, what);
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Over_the_harsh_desktop_every_word_on_the_dashboard_reads(string themeName)
        => OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var ground = HarshDesktop();
            LiquidGlassSources.Override = source => LiquidGlassProofTests.Behind(ground, source, W, H);
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver), theme);
            window.Width = W;
            window.Height = H;
            window.Show();
            using var motion = AeroMotion.Force(true);
            try
            {
                Pump(TimeSpan.FromMilliseconds(600));
                LiquidGlassProofTests.ClientOf(window, W, H);
                Pump(TimeSpan.FromMilliseconds(1500));
                window.UpdateLayout();
                Pump(TimeSpan.FromMilliseconds(300));
                var panes = GlassPanel.Live.ToArray();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (var k = 0; k < 10; k++) foreach (var piece in panes) piece.Ink.Evaluate();
                output.WriteLine($"every piece's ink looked at again ({panes.Length} pieces, {panes.Count(p => p.Ink.Zones > 0)} with regions): {watch.Elapsed.TotalMilliseconds / 10:0.0} ms");
                var measured = Measure(ground, window, Inks(window, window), $"aero-ink-harsh-dashboard-{theme}.png");
                Report($"Dashboard ({theme}) over the harsh desktop", measured);
                measured.Count.ShouldBeGreaterThan(40);
                EachTookTheBetterInk(measured);
                // The window's capsule: Home, and the bell and overlay icons.
                foreach (var name in new[] { "'Home'", "icon in BellButton", "icon in OverlayButton" })
                    measured.Single(m => m.Inked.Name == name).Median.ShouldBeGreaterThanOrEqualTo(4.5, name);
            }
            finally
            {
                window.CloseForSwitch();
                LiquidGlassSources.Override = null;
                NoCapture();
            }
        });

    /// <summary>
    /// Over Windows' own dark bloom (img19, the Aero video's desktop, the look's dark default), the look keeps its white:
    /// no piece or region of clear glass turns dark (the lime glass of an accent action lays its own, its words the
    /// accent's ink either way); only words over the bloom's brightest cells, past HeldFrom where the look's own 60 % step
    /// reads under 3:1, hold their white denser. Skipped where Windows has no img19.
    /// </summary>
    [Fact]
    public void Over_windows_dark_bloom_the_look_is_its_own()
        => OnUi(() =>
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper", "Windows", "img19.jpg");
            if (!File.Exists(path)) return;
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.UriSource = new Uri(path);
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.EndInit();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(picture, new Rect(0, 0, W, H));
            var ground = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            ground.Render(visual);
            ground.Freeze();
            LiquidGlassSources.Override = source => LiquidGlassProofTests.Behind(ground, source, W, H);
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver), Theme.Dark);
            window.Width = W;
            window.Height = H;
            window.Show();
            using var motion = AeroMotion.Force(true);
            try
            {
                Pump(TimeSpan.FromMilliseconds(600));
                LiquidGlassProofTests.ClientOf(window, W, H);
                Pump(TimeSpan.FromMilliseconds(1500));
                var pieces = MidnightHost.AllOf<GlassPanel>(window).ToList();
                pieces.Count.ShouldBeGreaterThan(30);
                var clear = pieces.Where(p => GlassInk.Tint(p.Background).A == 0).ToList();
                var dark = clear.Where(p => p.Ink.Laid == true || p.Ink.DarkZones > 0).Select(p => p.Name).ToList();
                var held = clear.Where(p => p.Ink.Laid == false).Select(p => p.Name).ToList();
                output.WriteLine($"over img19: {pieces.Count} pieces, {clear.Count} clear; held whole: {held.Count} ({string.Join(", ", held)}); "
                    + $"regions held: {clear.Sum(p => p.Ink.Zones)} in {clear.Count(p => p.Ink.Zones > 0)} pieces; turned dark: {dark.Count}");
                dark.ShouldBeEmpty("clear glass over the dark bloom keeps the white ink");
            }
            finally
            {
                window.CloseForSwitch();
                LiquidGlassSources.Override = null;
                NoCapture();
            }
        });

    /// <summary>A ledger as the Apps page lays it, in the page's styles, where the Apps page lies in the window: a pane with
    /// its title and total, Today, Week and Month on the sliding pills (Today chosen), the column heads, and rows of an icon,
    /// a name, the kWh, a share bar and its percent, and the cost.</summary>
    private static (Window Window, GlassPanel Pane) Ledger()
    {
        TextBlock Cell(string text, string style, int column, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var cell = new TextBlock { Text = text, HorizontalAlignment = align };
            cell.SetResourceReference(FrameworkElement.StyleProperty, style);
            Grid.SetColumn(cell, column);
            return cell;
        }
        Grid Columns() => new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(40) }, new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 140 },
                new ColumnDefinition { Width = new GridLength(76) }, new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 120, MaxWidth = 300 },
                new ColumnDefinition { Width = new GridLength(88) }, new ColumnDefinition { Width = new GridLength(30) },
            },
        };
        var track = new SegTrack();
        track.SetResourceReference(FrameworkElement.StyleProperty, "A.Page.SegTrack");
        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (word, chosen) in new[] { ("Today", true), ("Week", false), ("Month", false) })
        {
            var pill = new RadioButton { Content = word, GroupName = "Ledger", IsChecked = chosen };
            pill.SetResourceReference(FrameworkElement.StyleProperty, "A.SegItem");
            pills.Children.Add(pill);
        }
        track.Content = pills;
        DockPanel.SetDock(track, Dock.Right);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Cell("Apps", "A.PanelTitle", 0), Cell("1.83 kWh, Rs63.89 so far today", "A.Label", 0) } };
        var head = new DockPanel { Children = { track, title } };
        head.SetResourceReference(FrameworkElement.StyleProperty, "A.Page.Head");
        var heads = Columns();
        heads.Children.Add(Cell("App", "A.Page.Head.Cell", 1));
        heads.Children.Add(Cell("kWh", "A.Page.Head.Cell", 2, HorizontalAlignment.Right));
        var share = Cell("Share", "A.Page.Head.Cell", 3);
        share.Margin = new Thickness(16, 0, 0, 0);
        heads.Children.Add(share);
        var cost = Cell("Cost", "A.Page.Head.Cell", 4);
        cost.TextAlignment = TextAlignment.Right;
        heads.Children.Add(cost);
        var header = new Border { Child = heads };
        header.SetResourceReference(FrameworkElement.StyleProperty, "A.Table.Header");
        var rows = new StackPanel { Children = { head, header } };
        var apps = new[] { ("Google Chrome", "0.344", 19), ("Visual Studio Code", "0.221", 12), ("Microsoft Edge", "0.108", 6), ("Spotify", "0.044", 2),
            ("Windows Media Player", "0.024", 1), ("Git Bash", "0.009", 1), ("Programs that already closed", "0.015", 1), ("Other apps", "0.011", 1),
            ("Screen", "0.402", 22), ("Windows and idle", "0.365", 20), ("Rest of your PC", "0.282", 15) };
        foreach (var (name, kwh, percent) in apps)
        {
            var line = Columns();
            line.Margin = new Thickness(14, 10, 14, 10);
            line.Children.Add(new Icon { Size = 20, HorizontalAlignment = HorizontalAlignment.Left });
            line.Children.Add(Cell(name, "A.Page.Cell", 1));
            line.Children.Add(Cell(kwh, "A.Page.Cell.Number", 2));
            var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var trackLine = new Border { CornerRadius = new CornerRadius(2) };
            trackLine.SetResourceReference(Border.BackgroundProperty, "A.B.Track");
            var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 120 * percent / 22.0 };
            fill.SetResourceReference(Border.BackgroundProperty, "A.B.Accent");
            bar.Children.Add(trackLine);
            bar.Children.Add(fill);
            var percentText = Cell($"{percent}%", "A.Page.Cell.Number", 0);
            percentText.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
            percentText.Width = 44;
            percentText.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(percentText, Dock.Right);
            var shareCell = new DockPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { percentText, bar } };
            Grid.SetColumn(shareCell, 3);
            line.Children.Add(shareCell);
            var money = Cell($"Rs{double.Parse(kwh) * 35:0.00}", "A.Page.Cell.Number", 4);
            money.TextAlignment = TextAlignment.Right;
            line.Children.Add(money);
            rows.Children.Add(line);
        }
        var pane = new GlassPanel { Content = rows, Margin = new Thickness(275, 96, 15, 15), Padding = new Thickness(20, 18, 20, 12) };
        pane.SetResourceReference(FrameworkElement.StyleProperty, "A.Page.Glass");
        var window = AeroHost.Dressed(new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, Width = W, Height = H, Background = Brushes.Transparent, Content = pane, UseLayoutRounding = true,
        }, Theme.Dark);
        window.Resources.MergedDictionaries.Add(new SharedDictionary { Source = new Uri("pack://application:,,,/PowerLedger;component/Aero/Styles.Aero.Pages.xaml", UriKind.Absolute) });
        window.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "A.F.Ui");
        foreach (var icon in MidnightHost.AllOf<Icon>(pane)) icon.Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Bell");
        return (window, pane);
    }

    [Fact]
    public void Over_the_harsh_desktop_every_word_of_a_ledger_reads()
        => OnUi(() =>
        {
            var ground = HarshDesktop();
            LiquidGlassSources.Override = source => LiquidGlassProofTests.Behind(ground, source, W, H);
            var (window, pane) = Ledger();
            var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
            window.Show();
            using var motion = AeroMotion.Force(true);
            try
            {
                Pump(TimeSpan.FromMilliseconds(1200));
                window.UpdateLayout();
                Pump(TimeSpan.FromMilliseconds(300));
                var measured = Measure(ground, window, Inks(window, pane), "aero-ink-harsh-ledger.png");
                Report("A ledger over the harsh desktop", measured);
                measured.Count.ShouldBeGreaterThan(50);
                EachTookTheBetterInk(measured);
                measured.Single(m => m.Inked.Name == "'Today'").Median.ShouldBeGreaterThanOrEqualTo(4.5, "Today, chosen, on its sliding pill");
            }
            finally
            {
                material.Dispose();
                window.Close();
                LiquidGlassSources.Override = null;
                NoCapture();
            }
        });
}
