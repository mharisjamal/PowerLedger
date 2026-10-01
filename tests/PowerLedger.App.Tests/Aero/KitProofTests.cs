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
            T At<T>(T element, double x, double y, double w = double.NaN, double h = double.NaN)
                where T : FrameworkElement
            {
                Canvas.SetLeft(element, x);
                Canvas.SetTop(element, y);
                if (!double.IsNaN(w)) element.Width = w;
                if (!double.IsNaN(h)) element.Height = h;
                canvas.Children.Add(element);
                return element;
            }
            TextBlock Text(string text, double size = 15, FontWeight? weight = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.SemiBold };
            Style S(string key) => (Style)window.FindResource(key);
            Button Btn(string key, object content) => new() { Style = S(key), Content = content };

            foreach (var (x, y, w, h, title) in new[] { (48.0, 112.0, 660.0, 360.0, "Buttons and pills: rest, hover, pressed"), (732, 112, 660, 360, "Move pill and scrollbar: hidden, near, over"),
                         (48, 496, 660, 364, "Menu and switch"), (732, 496, 660, 364, "Watts overlay") })
            {
                At(new GlassPanel(), x, y, w, h);
                At(Text(title), x + 26, y + 26);
            }
            var rest = At(Btn("A.GlassBtn", "Overlay"), 74, 173, 90, 46);
            var hover = At(Btn("A.GlassBtn", "Overlay"), 180, 173, 90, 46);
            var press = At(Btn("A.GlassBtn", "Overlay"), 286, 173, 90, 46);
            var bell = At(Btn("A.RoundGlassBtn", new Icon { Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Bell"), Size = 18 }), 74, 241, 48, 48);
            var bellHover = At(Btn("A.RoundGlassBtn", new Icon { Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Bell"), Size = 18 }), 138, 241, 48, 48);
            At(Btn("A.AccentBtn", "Open report"), 202, 244, 116, 42);
            At(Btn("A.GhostBtn", "Switch back"), 334, 244, 117, 42);
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var word in new[] { "Day", "Week", "Month", "Year" }) seg.Children.Add(new RadioButton { Style = S("A.SegOpt"), Content = word, IsChecked = word == "Day", GroupName = "kit" });
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
            unit.Margin = new Thickness(6, 4, 0, 0);
            unit.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
            pill.Children.Add(unit);
            // The kit's sparkline: its path's ten points (y 18, 16, 19, 12, 14, 9, 13, 6, 10, 7 of 26), oldest first.
            double[] heights = [18, 16, 19, 12, 14, 9, 13, 6, 10, 7];
            var spark = new PowerLedger.App.Aero.Sparkline { Width = 90, Height = 26, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Seconds = 30,
                Samples = [.. heights.Select((y, i) => new SparkSample((heights.Length - 1 - i) * 30.0 / (heights.Length - 1), 26 - y))] };
            spark.SetResourceReference(PowerLedger.App.Aero.Sparkline.StrokeProperty, "A.B.Accent");
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
                if (((MenuItem)items.Children[0]).Template.FindName("Face", (MenuItem)items.Children[0]) is Border face) face.SetResourceReference(Border.BackgroundProperty, "A.B.GlassHover");
                UiHarness.Pump(TimeSpan.FromMilliseconds(1200));
                window.UpdateLayout();
                var outDir = Path.Combine(dir, "pages");
                Directory.CreateDirectory(outDir);
                LiquidGlassProofTests.Write(LiquidGlassProofTests.Over(ground, window, 1440, 900), Path.Combine(outDir, "Kit-Dark.png"));
            }
            finally
            {
                window.Close();
                LiquidGlassSources.Override = null;
                UiHarness.NoCapture();
            }
        });
    }
}
