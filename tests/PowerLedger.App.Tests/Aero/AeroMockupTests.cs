using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using AeroDashboard = PowerLedger.App.Aero.DashboardView;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.9's gap audit against the liquid glass mockup (Main, Styles and Kit, measured in headless Edge at 1440 by 900):
/// each difference the audit closed, held. The values are the boards' own; where Edge was measured, the numbers say so.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AeroMockupTests
{
    /// <summary>The Dashboard at the board's 1440 by 900, at rest.</summary>
    private static void OnDashboard(Action<AeroWindow, AeroDashboard> check)
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(true);
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                LiquidGlassProofTests.ClientOf(window, 1440, 900);
                UiHarness.PumpUntil(() => window.PageHost.Showing is AeroDashboard { IsLoaded: true }, TimeSpan.FromSeconds(10), "the Dashboard");
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                window.UpdateLayout();
                check(window, (AeroDashboard)window.PageHost.Showing!);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    private static Rect Box(FrameworkElement e, Visual root) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));

    /// <summary>Main's pieces the audit found off: the Ctrl K key 80 by 32 (60 wide and 10 each side), Open report 124 by 36,
    /// Energy each day's track 232 by 42 with Day's bubble 72 wide, its words 14 medium without a text shadow.</summary>
    [Fact]
    public void The_key_the_lime_action_and_the_span_track_are_the_mockups_size()
        => OnDashboard((window, view) =>
        {
            var key = UiHarness.Find<TextBlock>(window, t => t.Text == "Ctrl K")!;
            var keyGlass = UiHarness.Find<GlassPanel>((DependencyObject)window.FindName("SearchGlass"), g => g.Width == 80)!;
            keyGlass.RenderSize.ShouldBe(new Size(80, 32));
            key.Effect.ShouldBeNull("the mockup's key has no .t");
            var report = (Button)view.FindName("OpenReport");
            report.RenderSize.Width.ShouldBe(124, 0.5);
            report.RenderSize.Height.ShouldBe(36, 0.5);
            var day = UiHarness.Find<RadioButton>(view, r => (string)r.Content == "Day")!;
            day.ActualWidth.ShouldBe(72, 0.5);
            day.FontSize.ShouldBe(14);
            day.FontWeight.ShouldBe(FontWeights.Medium);
            var track = UiHarness.Find<GlassPanel>(view, g => g.Width == 232)!;
            track.RenderSize.Height.ShouldBe(42, 0.5);
            ((FrameworkElement)view.FindName("SegIndicator")).ActualWidth.ShouldBe(72, 0.5, "the bubble on Day");
        });

    /// <summary>The sidebar's column has a 4 px gap: before "Your PCs" (26 over it, so 30) and before each PC's 40 high row,
    /// 44 apart.</summary>
    [Fact]
    public void Your_pcs_keep_the_columns_gap()
        => OnDashboard((window, _) =>
        {
            var rows = MidnightHost.AllOf<Button>((DependencyObject)window.FindName("Pcs")).ToList();
            rows.Count.ShouldBeGreaterThanOrEqualTo(2);
            (Box(rows[1], window).Y - Box(rows[0], window).Y).ShouldBe(44, 0.9);
            rows[0].Margin.Top.ShouldBe(4);
            UiHarness.Find<TextBlock>(window, t => t.Text == "Your PCs")!.Margin.Top.ShouldBe(30);
        });

    /// <summary>Power now's grid row is 252 high in the mockup (its wells 171, 14 and 67 run 8 past the pane's padding),
    /// and Last minute's drawing is the SVG's 186 high, its view box laid in as SVG's meet lays it.</summary>
    [Fact]
    public void Power_nows_wells_and_last_minute_are_the_mockups_height()
        => OnDashboard((window, view) =>
        {
            ((FrameworkElement)view.FindName("NowContent")).ActualHeight.ShouldBe(252, 0.9);
            var live = (LiveChart)view.FindName("Live");
            live.ActualHeight.ShouldBe(186, 0.5);
            ((FrameworkElement)view.FindName("TodayKwh")).Parent.ShouldBeOfType<StackPanel>().VerticalAlignment.ShouldBe(VerticalAlignment.Top);
        });

    /// <summary>The mockup's big figures are tracked in by .035 em (its .num), and set in proportional digits.</summary>
    [Fact]
    public void The_big_figures_are_tracked_as_the_mockups()
        => OnDashboard((window, view) =>
        {
            ((RollingNumber)view.FindName("NowRoll")).Tracking.ShouldBe(-0.035);
            var month = (TrackedText)view.FindName("MonthBig");
            month.Tracking.ShouldBe(-0.035);
            month.FontSize.ShouldBe(42);
            month.Margin.Top.ShouldBe(19);
        });

    /// <summary>A tracked line: each character at its advance and the tracking, as CSS's letter-spacing sets it (the space
    /// after every character, the last's too).</summary>
    [Fact]
    public void Tracked_text_is_as_wide_as_the_browsers()
        => UiHarness.OnUi(() =>
        {
            var family = new FontFamily(new Uri("pack://application:,,,/PowerLedger;component/"), "./Fonts/Geist/#Geist");
            var text = new TrackedText { Text = "$5.73", FontSize = 42, FontWeight = FontWeights.SemiBold, FontFamily = family, Tracking = -0.035 };
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var face = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var sum = "$5.73".Sum(c => new FormattedText(c.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 42, Brushes.White, 1).WidthIncludingTrailingWhitespace);
            text.DesiredSize.Width.ShouldBe(sum - 5 * 0.035 * 42, 0.01);
        });

    /// <summary>Energy each day's columns as the mockup's flex row: each its label's width and an equal share of the rest,
    /// so day 10's bar is wider than day 1's by their labels' difference (Edge: 34.9 and 42.2).</summary>
    [Fact]
    public void Energy_each_days_columns_are_the_mockups_flex_row()
        => OnDashboard((window, view) =>
        {
            var chart = (EnergyBarChart)view.FindName("Bars");
            chart.Bars = [.. MockupData.Days.Select((kwh, i) => new EnergyBar(new DateOnly(2026, 9, 1 + i), (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), kwh, ""))];
            UiHarness.Pump(TimeSpan.FromMilliseconds(200));
            chart.UpdateLayout();
            var bars = chart.BarPieces;
            var labels = MidnightHost.AllOf<TextBlock>(chart).Where(t => t.Text.Length > 0 && char.IsDigit(t.Text[0])).ToList();
            (bars[9].ActualWidth - bars[0].ActualWidth).ShouldBe(labels[9].ActualWidth - labels[0].ActualWidth, 0.9);
            bars[13].Lift.ShouldBe(GlassLift.Bar, "the bar's own box-shadow, 0 8px 16px -10px");
            bars[13].Bubble.ShouldBeFalse();
        });

    /// <summary>The dark theme's ink is the mockup's white (#fff, its .sub at 80 %, the sidebar's pages at 88 %), not the
    /// older demo's #F3F4F6.</summary>
    [Fact]
    public void The_dark_ink_is_the_mockups_white()
        => UiHarness.OnUi(() =>
        {
            var palette = ThemeManager.Palette(Look.Aero, Theme.Dark);
            ((Color)palette["A.C.Text"]).ShouldBe(Colors.White);
            ((Color)palette["A.C.Text2"]).ShouldBe(Color.FromArgb(0xCC, 255, 255, 255));
            ((Color)palette["A.C.NavText"]).ShouldBe(Color.FromArgb(0xE0, 255, 255, 255));
            ((Color)palette["A.C.LabText"]).ShouldBe(Color.FromArgb(0xE6, 255, 255, 255), "the Styles board's .lab");
            ((Color)palette["A.C.CapText"]).ShouldBe(Color.FromArgb(0xBF, 255, 255, 255), "the Styles board's .cap");
            ((Color)palette["A.C.MarkerFill"]).ShouldBe(Color.FromRgb(0x0C, 0x16, 0x40), "Last minute's end dot");
            ((Color)palette["A.C.AccentLow"]).ShouldBe(Color.FromRgb(0xC9, 0xE6, 0x3E));
            ((Color)palette["A.C.AccentHigh"]).ShouldBe(Color.FromRgb(0xEA, 0xFF, 0x7A));
            ((Color)palette["A.C.AccentLine"]).ShouldBe(Color.FromRgb(0xE8, 0xFF, 0x78));
            ((Color)palette["A.C.AccentLineFill"]).ShouldBe(Color.FromRgb(0xE2, 0xFB, 0x66));
            GlassMaterial.Shifted(Color.FromRgb(0xD9, 0xF2, 0x5A), GlassMaterial.AccentShades[1].Shift).ShouldBe((Color)palette["A.C.AccentHigh"], "the steps are the lime's");
        });

    /// <summary>
    /// A box-shadow as the browser draws one, round corners and all (Edge, black at 50 %, 0 8px 18px -8px under a 216 by 46
    /// capsule: 64, 53, 42, 32, 24, 17, 12, 8, 5, 3 of 255 every 2 px below its middle; 27, 22, 17, 13, 9, 6 below x 10,
    /// where the rectangle of 0.10.8 had nearly the middle's).
    /// </summary>
    [Fact]
    public void A_box_shadow_is_the_browsers_round_cornered_one()
        => UiHarness.OnUi(() =>
        {
            var lift = new GlassLift(8, 18, -8, Color.FromArgb(0x80, 0, 0, 0));
            var map = GlassShadow.BubbleMap(216, 46, 23, lift);
            var reach = lift.Reach;
            var pixels = new byte[map.PixelWidth * map.PixelHeight * 4];
            map.CopyPixels(pixels, map.PixelWidth * 4, 0);
            // The map is laid from -reach and dropped by Y: a point of the page (x, y) from the piece's corner reads column
            // x + reach, row y + reach - Y (pixel centres alike).
            byte At(double x, double y) => pixels[((int)(y + reach - lift.Y) * map.PixelWidth + (int)(x + reach)) * 4 + 3];
            byte[] middle = [64, 53, 42, 32, 24, 17, 12, 8, 5, 3];
            for (var i = 0; i < middle.Length; i++) ((double)At(108, 46 + 2 * i)).ShouldBe(middle[i], 3, $"below the middle, {2 * i} px");
            byte[] side = [27, 22, 17, 13, 9, 6];
            for (var i = 0; i < side.Length; i++) ((double)At(10, 46 + 2 * i)).ShouldBe(side[i], 3, $"below x 10, {2 * i} px");
        });

    /// <summary>
    /// CSS's drop-shadow filter shadows what the element paints, its content too (Edge's board is 2 to 5 levels darker for
    /// it round the panes). A piece whose content covers it entirely casts as a fully tinted piece does.
    /// </summary>
    [Fact]
    public void A_pieces_content_casts_the_recipes_drop_shadow()
        => UiHarness.OnUi(() =>
        {
            var window = AeroHost.Dressed(new Window { Width = 600, Height = 500, WindowStyle = WindowStyle.None, Left = -20000, ShowActivated = false, ShowInTaskbar = false }, Theme.Dark);
            var panel = new GlassPanel { Width = 300, Height = 200, Padding = new Thickness(0), Content = new Border { Background = Brushes.Black }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            window.Content = new Grid { Children = { panel } };
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var content = GlassShadow.ContentDropFor(panel, 0).ShouldNotBeNull();
                var full = GlassShadow.DropMapFor(300, 200, 1, 0).ShouldNotBeNull();
                content.PixelWidth.ShouldBe(full.PixelWidth);
                byte[] Alpha(BitmapSource b)
                {
                    var px = new byte[b.PixelWidth * b.PixelHeight * 4];
                    b.CopyPixels(px, b.PixelWidth * 4, 0);
                    return [.. px.Where((_, i) => i % 4 == 3)];
                }
                var (a, f) = (Alpha(content), Alpha(full));
                Enumerable.Range(0, a.Length).Max(i => Math.Abs(a[i] - f[i])).ShouldBeLessThanOrEqualTo(3);
                f.Max().ShouldBeGreaterThan((byte)60, "a real shadow");
                ((FrameworkElement)panel.Template.FindName("PART_Shadow", panel) is GlassShadow shadow && shadow.ContentDropMap is not null)
                    .ShouldBeTrue("the piece worked its content's shadow out once it settled");
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>The kit's controls: a glass pill (rest 12 % white, its own box-shadow and no drop-shadow filter); the move
    /// pill 72 by 8 under the pointer; the Styles board's choices 14 medium in white; a menu's items at 88 %, white when
    /// highlighted; controls' words 14.</summary>
    [Fact]
    public void The_kits_controls_are_the_kits()
        => UiHarness.OnUi(() =>
        {
            var palette = ThemeManager.Palette(Look.Aero, Theme.Dark);
            ((double)palette["A.T.Control"]).ShouldBe(14);
            GrabBar.OverWidth.ShouldBe(72);
            GrabBar.OverHeight.ShouldBe(8);
            GlassButton.LiftProperty.DefaultMetadata.DefaultValue.ShouldBe(GlassLift.Pill);
            GlassButton.CastsProperty.DefaultMetadata.DefaultValue.ShouldBe(false);
            var window = AeroHost.Dressed(new Window { Width = 400, Height = 300, WindowStyle = WindowStyle.None, Left = -20000, ShowActivated = false, ShowInTaskbar = false }, Theme.Dark);
            var pill = new Button { Style = (Style)window.FindResource("A.GlassBtn"), Content = "Overlay" };
            var lime = new Button { Style = (Style)window.FindResource("A.AccentBtn"), Content = "Open report" };
            var option = new RadioButton { Style = (Style)window.FindResource("A.SegOpt"), Content = "Tinted" };
            var item = new MenuItem { Style = (Style)window.FindResource("A.MenuItem"), Header = "Add a PC" };
            window.Content = new StackPanel { Children = { pill, lime, option, item } };
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                var glass = (GlassPanel)pill.Template.FindName("Glass", pill);
                glass.HasShadow.ShouldBeFalse();
                glass.Lift.ShouldBe(GlassLift.Pill);
                ((SolidColorBrush)glass.Background).Color.ShouldBe(Color.FromArgb(0x1F, 255, 255, 255));
                var limeGlass = (GlassPanel)lime.Template.FindName("Glass", lime);
                limeGlass.HasShadow.ShouldBeTrue("Main's .lg.cap casts the recipe's shadow");
                limeGlass.Lift.ShouldBeNull();
                option.FontSize.ShouldBe(14);
                option.FontWeight.ShouldBe(FontWeights.Medium);
                ((SolidColorBrush)option.Foreground).Color.ShouldBe(Colors.White);
                ((SolidColorBrush)item.Foreground).Color.ShouldBe(Color.FromArgb(0xE0, 255, 255, 255));
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>The kit's watts pill: the unit 14 after the watts (the pill's gap), the sparkline a line 2 wide in the
    /// accent's lit head with nothing under it.</summary>
    [Fact]
    public void The_overlay_is_the_kits_pill()
        => UiHarness.OnUi(() =>
        {
            var now = MidnightFixtures.NowScreen(out _);
            var settings = MidnightFixtures.SettingsScreen(new FakeUiSettings());
            var overlay = AeroHost.Dressed(new OverlayWindow(now, settings) { Placing = false, Left = -20000, Top = 0 }, Theme.Dark);
            try
            {
                overlay.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                ((TextBlock)overlay.FindName("Unit")).Margin.Left.ShouldBe(14);
                var spark = (PowerLedger.App.Aero.Sparkline)overlay.FindName("Spark");
                ((SolidColorBrush)spark.Stroke!).Color.ShouldBe(Color.FromRgb(0xEA, 0xFF, 0x7A));
                spark.OpacityMask.ShouldBeNull("no fade: the kit's line is whole");
                System.Windows.Documents.Typography.GetNumeralAlignment(overlay).ShouldBe(FontNumeralAlignment.Normal);
            }
            finally
            {
                overlay.Close();
            }
        });
}
