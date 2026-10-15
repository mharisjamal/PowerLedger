using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AE in the window and on the glass: the Apps page and the Dashboard's top app line drawn in Aero's window over a
/// desktop the engine is handed (the mockup's own, a white window open on it, in the dark theme; a pale one in the light),
/// as the see-through window shows the real desktop; and the ledger's words measured for contrast with the adaptive ink
/// over a white and a black desktop. Renders go to %TEMP%\powerledger-renders.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AppsGlassRenderingTests(ITestOutputHelper output)
{
    /// <summary>The mockup's desktop (Main.dc.html): its four glows on deep navy, and the white Photos window open on it,
    /// at 1440 by 900 scaled to <paramref name="width"/> by <paramref name="height"/>.</summary>
    private static BitmapSource MockupDesktop(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(width / 1440.0, height / 900.0));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x16, 0x40)), null, new Rect(0, 0, 1440, 900));
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
            // The Photos window, white, as the mockup has it open behind the glass.
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xFB)), null, new Rect(560, 120, 640, 470), 16, 16);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xF1)), null, new Rect(560, 120, 640, 40));
            dc.Pop();
        }
        return Frozen(visual, width, height);
    }

    /// <summary>A pale desktop for the light theme: the page renders' own glows.</summary>
    private static BitmapSource PaleDesktop(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(AeroPages.Backdrop(Theme.Light), null, new Rect(0, 0, width, height));
        return Frozen(visual, width, height);
    }

    private static BitmapSource Frozen(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The shell on <paramref name="page"/> in a 1440 by 900 Aero window over <paramref name="ground"/>, drawn to
    /// <paramref name="name"/> as the see-through window shows it.</summary>
    private static void InWindow(BitmapSource ground, Theme theme, Page page, string name, Action<AeroWindow, ShellViewModel>? act = null)
    {
        const int W = 1440, H = 900;
        LiquidGlassSources.Override = source => LiquidGlassProofTests.Behind(ground, source, W, H);
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        var window = AeroHost.Window(shell, theme);
        using var motion = AeroMotion.Force(true);
        window.Width = W;
        window.Height = H;
        window.Show();
        try
        {
            Pump(TimeSpan.FromMilliseconds(600));
            LiquidGlassProofTests.ClientOf(window, W, H);
            shell.Page = page;
            Pump(TimeSpan.FromMilliseconds(900));
            act?.Invoke(window, shell);
            Pump(TimeSpan.FromMilliseconds(600));
            window.UpdateLayout();
            LiquidGlassProofTests.Write(LiquidGlassProofTests.Over(ground, window, W, H), Path.Combine(Folder, name));
        }
        finally
        {
            window.CloseForSwitch();
            LiquidGlassSources.Override = null;
            NoCapture();
        }
    }

    [Fact]
    public void The_apps_page_and_the_dashboards_top_app_in_the_window_over_a_desktop()
    {
        OnUi(() =>
        {
            foreach (var theme in AeroPages.Themes)
            {
                var ground = theme == Theme.Dark ? MockupDesktop(1440, 900) : PaleDesktop(1440, 900);
                InWindow(ground, theme, Page.Apps, $"aero-apps-window-{theme}.png", (window, shell) =>
                {
                    window.PageHost.Showing.ShouldBeOfType<AppsView>();
                    PumpUntil(() => MidnightHost.AllOf<AppIcon>(window.PageHost.Showing!).Where(i => i.Path is { } p && File.Exists(p)).All(i => i.Image is not null),
                        TimeSpan.FromSeconds(10), "the programs' icons");
                    shell.Apps!.Toggle.Execute(shell.Apps.Rows[2].Key);   // Microsoft Edge opened
                });
                InWindow(ground, theme, Page.Dashboard, $"aero-dashboard-window-{theme}.png", (window, _) =>
                    ((Button)((FrameworkElement)window.PageHost.Showing!).FindName("TopApp")).IsVisible.ShouldBeTrue());
            }
        });
        foreach (var name in new[] { "aero-apps-window-Dark.png", "aero-apps-window-Light.png", "aero-dashboard-window-Dark.png", "aero-dashboard-window-Light.png" })
            new FileInfo(Path.Combine(Folder, name)).Length.ShouldBeGreaterThan(200_000, name);
    }

    /// <summary>
    /// Plan AD's adaptive ink on the ledger: over a white desktop the pane's words turn dark and read, over a black one
    /// they are the look's own white and read. Each word's best contrast against the same spot drawn without it: the
    /// title, the figures and the names at WCAG AA's 4.5:1, the quieter steps too, the quietest (the column heads) at the
    /// 3:1 the ink holds them to.
    /// </summary>
    [Theory]
    [InlineData(255)]
    [InlineData(0)]
    public void Over_a_white_and_a_black_desktop_the_ledgers_words_read(byte grey)
        => OnUi(() =>
        {
            const int W = 900, H = 660;
            var ground = FakeGlassSource.Picture(W, H, (_, _) => (grey, grey, grey));
            LiquidGlassSources.Override = source => LiquidGlassProofTests.Behind(ground, source, W, H);
            var model = AppsFixtures.Screen();
            model.Show();
            var view = new AppsView { DataContext = model };
            var window = AeroHost.Dressed(new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, Width = W, Height = H, Background = Brushes.Transparent, Content = view, UseLayoutRounding = true,
            }, Theme.Dark);
            window.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "A.F.Ui");
            var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
            using var motion = AeroMotion.Force(true);
            window.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(800));
                window.UpdateLayout();
                var lines = (ItemsControl)view.FindName("Lines");
                var first = (FrameworkElement)lines.ItemContainerGenerator.ContainerFromIndex(0);
                var closed = (FrameworkElement)lines.ItemContainerGenerator.ContainerFromIndex(6);
                (string What, FrameworkElement Element, double Least)[] probes =
                [
                    ("title", Find<TextBlock>(view, t => t.Text == "Apps")!, 4.5),
                    ("total and cost", (TextBlock)view.FindName("Summary"), 4.5),
                    ("column head", Find<TextBlock>(view, t => t.Text == "App")!, 3),
                    ("app", Find<TextBlock>(first, t => t.Text == "Google Chrome")!, 4.5),
                    ("kWh", Find<TextBlock>(first, t => t.Text == "0.344")!, 4.5),
                    ("share", Find<TextBlock>(first, t => t.Text == "19%")!, 4.5),
                    ("line beside the apps", Find<TextBlock>(closed, t => t.Text == "Programs that already closed")!, 4.5),
                ];
                var with = Pixels(ground, window, W, H, out var bitmap);
                LiquidGlassProofTests.Write(bitmap, Path.Combine(Folder, $"aero-apps-ink-{(grey == 255 ? "white" : "black")}.png"));
                foreach (var probe in probes) probe.Element.Opacity = 0;
                var without = Pixels(ground, window, W, H, out _);
                foreach (var probe in probes) probe.Element.Opacity = 1;
                foreach (var (what, element, least) in probes)
                {
                    var contrast = Best(window, element, with, without, W);
                    output.WriteLine($"{(grey == 255 ? "white" : "black")}: {what} {contrast:0.0}:1");
                    contrast.ShouldBeGreaterThanOrEqualTo(least, what);
                }
            }
            finally
            {
                material.Dispose();
                window.Close();
                LiquidGlassSources.Override = null;
                NoCapture();
            }
        });

    private static byte[] Pixels(BitmapSource ground, Window window, int w, int h, out BitmapSource bitmap)
    {
        bitmap = LiquidGlassProofTests.Over(ground, window, w, h);
        var pixels = new byte[w * h * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    /// <summary>The best contrast any pixel of <paramref name="element"/>'s box makes against the same pixel drawn without it.</summary>
    private static double Best(Window window, FrameworkElement element, byte[] with, byte[] without, int w)
    {
        var box = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        var best = 1.0;
        for (var y = (int)box.Top; y < (int)box.Bottom; y++)
        {
            for (var x = (int)box.Left; x < (int)box.Right; x++)
            {
                var at = (y * w + x) * 4;
                best = Math.Max(best, Contrast.Ratio(Color.FromRgb(with[at + 2], with[at + 1], with[at]), Color.FromRgb(without[at + 2], without[at + 1], without[at])));
            }
        }
        return best;
    }
}
