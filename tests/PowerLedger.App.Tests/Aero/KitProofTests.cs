using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using PowerLedger.App.Aero;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.9: the Kit board's controls drawn by the App, each at the board's own place (measured from the board in Edge),
/// over the board's background handed to the engine, for 8x crops beside the board's. Drawn only with PL_PROOF_DIR set.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class KitProofTests
{
    [Fact]
    public void The_kits_controls_over_its_background()
    {
        if (Environment.GetEnvironmentVariable("PL_PROOF_DIR") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var background = Path.Combine(dir, "ref", "Kit-bg.png");
        if (!File.Exists(background)) return;
        UiHarness.OnUi(() =>
        {
            var ground = LiquidGlassProofTests.Load(background);
            LiquidGlassSources.Override = source => LiquidGlassProofTests.Behind(ground, source, 1440, 900);
            var canvas = new Canvas { Width = 1440, Height = 900 };
            var window = AeroHost.Dressed(new Window
            {
                Content = canvas, Width = 1440, Height = 900, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            }, Theme.Dark);
            window.SetResourceReference(TextElement.FontFamilyProperty, "A.F.Ui");
            window.SetResourceReference(TextElement.ForegroundProperty, "A.B.Text");
            using var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
            // Each of the board's panes is a glass piece holding its controls, as in the App (and in the browser, where a
            // pill inside a pane reads the pane's content beneath it, not the screen).
            var panes = new List<(Rect Box, Canvas Inside)>();
            T At<T>(T element, double x, double y, double w = double.NaN, double h = double.NaN)
                where T : FrameworkElement
            {
                if (!double.IsNaN(w)) element.Width = w;
                if (!double.IsNaN(h)) element.Height = h;
                var (box, inside) = panes.FirstOrDefault(p => p.Box.Contains(new Point(x, y)));
                var host = inside ?? canvas;
                Canvas.SetLeft(element, inside is null ? x : x - box.X);
                Canvas.SetTop(element, inside is null ? y : y - box.Y);
                host.Children.Add(element);
                return element;
            }
            TextBlock Text(string text, double size = 15, FontWeight? weight = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.SemiBold };
            Style S(string key) => (Style)window.FindResource(key);
            Button Btn(string key, object content) => new() { Style = S(key), Content = content };

            foreach (var (x, y, w, h, title) in new[] { (48.0, 112.0, 660.0, 360.0, "Buttons and pills: rest, hover, pressed"), (732, 112, 660, 360, "Move pill and scrollbar: hidden, near, over"),
                         (48, 496, 660, 364, "Menu and switch"), (732, 496, 660, 364, "Watts overlay") })
            {
                var inside = new Canvas { ClipToBounds = false };
                At(new GlassPanel { Content = inside }, x, y, w, h);
                panes.Add((new Rect(x, y, w, h), inside));
                At(Text(title), x + 26, y + 26);
            }
            var rest = At(Btn("A.GlassBtn", "Overlay"), 74, 173, 90, 46);
            var hover = At(Btn("A.GlassBtn", "Overlay"), 180, 173, 90, 46);
            var press = At(Btn("A.GlassBtn", "Overlay"), 286, 173, 90, 46);
            var bell = At(Btn("A.RoundGlassBtn", new Icon { Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Bell"), Size = 18 }), 74, 241, 48, 48);
            var bellHover = At(Btn("A.RoundGlassBtn", new Icon { Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Bell"), Size = 18 }), 138, 241, 48, 48);
            // The kit's Open report is a .pill: its box-shadow, no drop-shadow filter (the Dashboard's is the Main board's).
            var kitReport = At(Btn("A.AccentBtn", "Open report"), 202, 244, 116, 42);
            GlassButton.SetCasts(kitReport, false);
            GlassButton.SetLift(kitReport, GlassLift.Pill);
            At(Btn("A.GhostBtn", "Switch back"), 334, 244, 117, 42);
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var word in new[] { "Day", "Week", "Month", "Year" }) seg.Children.Add(new RadioButton { Style = S("A.SegOpt"), Content = word, IsChecked = word == "Day", GroupName = "kit", Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(word == "Day" ? 0 : 2, 0, 0, 0) });
            At(new Border { Style = S("A.SegGroup"), Child = seg }, 74, 311);

            // The grab bar at rest, near and over; the scroll bar near and over (the thumb's glass, as its template draws it).
            GrabBar Grab(double x, Point? pointer)
            {
                var bar = At(new GrabBar { Style = S("A.GrabBar") }, x, 186 - (GrabBar.HitHeight - GrabBar.BarHeight) / 2);
                bar.Loaded += (_, _) => bar.Track(pointer);
                return bar;
            }
            Grab(758 - (GrabBar.HitWidth - 64) / 2, null);
            Grab(874 - (GrabBar.HitWidth - 64) / 2, new Point(GrabBar.HitWidth / 2, -40));
            Grab(1008 - (GrabBar.HitWidth - 72) / 2, new Point(GrabBar.HitWidth / 2, GrabBar.HitHeight / 2));
            At(new GlassPanel { CornerRadius = new CornerRadius(3), Opacity = 0.55 }, 908, 279, 6, 120);
            var over = At(new Grid(), 1068, 279, 10, 120);
            over.Children.Add(new GlassPanel { CornerRadius = new CornerRadius(5) });
            var wash = new Border { CornerRadius = new CornerRadius(5) };
            wash.SetResourceReference(Border.BackgroundProperty, "A.B.GlassHover");
            over.Children.Add(wash);

            // The menu, as A.Menu draws it, and the switch off and on.
            var items = new StackPanel { Margin = new Thickness(8) };
            foreach (var word in new[] { "Home, 2 PCs", "Add a PC", "Rename this PC", "-", "Leave the household" })
                items.Children.Add(word == "-" ? new Separator { Style = (Style)window.FindResource(MenuItem.SeparatorStyleKey) } : new MenuItem { Style = S("A.MenuItem"), Header = word });
            var menu = At(new GlassPanel { HasShadow = false, Content = items }, 74, 557, 296);
            menu.SetResourceReference(GlassPanel.CornerRadiusProperty, "A.R.Menu");
            menu.SetResourceReference(Control.BackgroundProperty, "A.B.MenuFill");
            At(new GlassSwitch { IsChecked = false }, 398, 565);
            At(new GlassSwitch { IsChecked = true }, 398, 619);

            // The watts overlay's pill, as its window draws it.
            var pill = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pill.Children.Add(new PulseDot { Size = 10, Breathes = false, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
            pill.Children.Add(Text("146", 26));
            var unit = Text("W", 16, FontWeights.Medium);
            unit.Margin = new Thickness(14, 0, 0, 0);
            unit.VerticalAlignment = VerticalAlignment.Center;
            unit.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
            pill.Children.Add(unit);
            // The kit's sparkline: its path's ten points (y 18, 16, 19, 12, 14, 9, 13, 6, 10, 7 of 26), oldest first.
            double[] heights = [18, 16, 19, 12, 14, 9, 13, 6, 10, 7];
            var spark = new PowerLedger.App.Aero.Sparkline { Width = 90, Height = 26, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Seconds = 30,
                Samples = [.. heights.Select((y, i) => new SparkSample((heights.Length - 1 - i) * 30.0 / (heights.Length - 1), 26 - y))] };
            spark.SetResourceReference(PowerLedger.App.Aero.Sparkline.StrokeProperty, "A.B.AccentHigh");
            pill.Children.Add(spark);
            At(new GlassPanel { Style = S("A.Glass.Capsule"), Padding = new Thickness(18, 0, 22, 0), HasShadow = false, Content = pill }, 758, 562, double.NaN, 58);
            var note = At(new TextBlock { Text = "Always on top, anywhere on screen. Right click for corner, opacity and the 30 second graph.", FontSize = 12.5, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap }, 758, 644, 540);
            note.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");

            window.Show();
            try
            {
                using var motion = AeroMotion.Force(true);
                UiHarness.Pump(TimeSpan.FromMilliseconds(800));
                foreach (var b in new ButtonBase[] { hover, bellHover })
                    ((FrameworkElement)b.Template.FindName("Hover", b)).Opacity = 1;
                press.RenderTransformOrigin = new Point(0.5, 0.5);
                press.RenderTransform = new ScaleTransform(AeroMotion.PressScale, AeroMotion.PressScale);
                if (((MenuItem)items.Children[0]).Template.FindName("Face", (MenuItem)items.Children[0]) is Border face)
                {
                    // The kit's first item as highlighted: the wash, its top light and white words.
                    face.SetResourceReference(Border.BackgroundProperty, "A.B.GlassHover");
                    face.BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255));
                    ((MenuItem)items.Children[0]).SetResourceReference(Control.ForegroundProperty, "A.B.Text");
                }
                UiHarness.Pump(TimeSpan.FromMilliseconds(1200));
                window.UpdateLayout();
                var outDir = Path.Combine(dir, "pages");
                Directory.CreateDirectory(outDir);
                LiquidGlassProofTests.Write(LiquidGlassProofTests.Over(ground, window, 1440, 900), Path.Combine(outDir, "Kit-Dark.png"));
                ProofInk.Write(ground, window, window, 1440, 900, Path.Combine(outDir, "Kit-Dark"));
            }
            finally
            {
                window.Close();
                LiquidGlassSources.Override = null;
                UiHarness.NoCapture();
            }
        });
    }

    /// <summary>The real watts overlay window, in both themes, reading the kit's 146 W and its sparkline, its pill laid at
    /// the kit's place (758, 562) over the kit's background as the engine is handed it, for the Overlay sheet.</summary>
    [Fact]
    public void The_overlay_window_over_the_kits_background()
    {
        if (Environment.GetEnvironmentVariable("PL_PROOF_DIR") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var background = Path.Combine(dir, "ref", "Kit-bg.png");
        if (!File.Exists(background)) return;
        var outDir = Path.Combine(dir, "pages");
        Directory.CreateDirectory(outDir);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            UiHarness.OnUi(() =>
            {
                var ground = LiquidGlassProofTests.Load(background);
                const double left = -20000, top = 0;
                var now = MidnightFixtures.NowScreen(out _);
                var settings = MidnightFixtures.SettingsScreen(new FakeUiSettings());
                OverlayWindow? overlay = null;
                try
                {
                    overlay = AeroHost.Dressed(new OverlayWindow(now, settings) { Placing = false, Left = left, Top = top }, theme);
                    var room = (Thickness)overlay.FindResource("Overlay.Room");
                    // The ground lies so that the pill's corner is at the kit's 758, 562.
                    LiquidGlassSources.Override = source =>
                    {
                        var scale = source.CompositionTarget.TransformToDevice.M11;
                        var at = (Window)source.RootVisual;
                        return new FakeGlassSource(ground, new Rect((at.Left + room.Left - 758) * scale, (at.Top + room.Top - 562) * scale, 1440 * scale, 900 * scale));
                    };
                    overlay.Apply(OverlaySettings.Default with { Enabled = true });
                    overlay.ShowOverlay();
                    UiHarness.Pump(TimeSpan.FromMilliseconds(600));
                    MockupData.Overlay(overlay);
                    UiHarness.Pump(TimeSpan.FromMilliseconds(1200));
                    overlay.UpdateLayout();
                    var root = (FrameworkElement)overlay.Content;
                    int w = (int)Math.Ceiling(root.ActualWidth + room.Left + room.Right), h = (int)Math.Ceiling(root.ActualHeight + room.Top + room.Bottom);
                    var app = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    app.Render(overlay);
                    var both = new DrawingVisual();
                    using (var dc = both.RenderOpen())
                    {
                        dc.DrawImage(ground, new Rect(0, 0, 1440, 900));
                        dc.DrawImage(app, new Rect(758 - room.Left, 562 - room.Top, w, h));
                    }
                    var result = new System.Windows.Media.Imaging.RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
                    result.Render(both);
                    LiquidGlassProofTests.Write(result, Path.Combine(outDir, $"Overlay-{theme}.png"));
                    var pill = (FrameworkElement)overlay.FindName("Glass");
                    var at = pill.TranslatePoint(new Point(0, 0), overlay);
                    File.WriteAllText(Path.Combine(outDir, $"Overlay-{theme}.json"), string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"[{{\"name\":\"Pill\",\"x\":{758 - room.Left + at.X:0.##},\"y\":{562 - room.Top + at.Y:0.##},\"w\":{pill.ActualWidth:0.##},\"h\":{pill.ActualHeight:0.##}}},{{\"name\":\"Window\",\"x\":{758 - room.Left:0.##},\"y\":{562 - room.Top:0.##},\"w\":{w},\"h\":{h}}}]"));
                    File.WriteAllText(Path.Combine(outDir, $"Overlay-{theme}-dom.json"), ProofInk.Dump(overlay));
                }
                finally
                {
                    overlay?.Close();
                    LiquidGlassSources.Override = null;
                    UiHarness.NoCapture();
                }
            });
        }
    }
}
