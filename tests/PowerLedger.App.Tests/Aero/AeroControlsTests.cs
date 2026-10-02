using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S G6: the rolling number, the pulse dot and the colour wheel: what each shows, how it moves (and when it
/// doesn't), how the keyboard and a screen reader reach it; and renders of the three in both themes
/// (%TEMP%\powerledger-renders\aero-controls-*).
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process (agent D's AeroMotionScope)
public class AeroControlsTests
{
    [Fact]
    public void A_rolling_number_rolls_each_digit_to_its_value()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(true);
            var number = new RollingNumber { DigitSize = 40 };
            number.Text.ShouldBe("0");
            number.Set(142);
            number.Text.ShouldBe("142");
            number.Children.Count.ShouldBe(3);
            number.Rolled.ShouldBe([1.0, 4.0, 2.0]);
            number.LineHeight.ShouldBe(50);
            number.Value = 7;
            number.Rolled.ShouldBe([7.0], "fewer digits, fewer windows");
            number.Set(-5);
            number.Text.ShouldBe("0", "no negative watts");
        });

    /// <summary>0.10.9's audit: Power now's watts drew black in the dark theme. The number set its own numeral alignment,
    /// a local inheritable value, so WPF kept the text colour it had when it was built off the tree (the page host builds
    /// the Dashboard first, its colour from a style reached later). Now its digits take the colour round them wherever
    /// and whenever they join.</summary>
    [Fact]
    public void A_rolling_number_takes_the_text_colour_round_it_when_it_joins_later()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(true);
            window.Show();
            try
            {
                UiHarness.PumpUntil(() => window.PageHost.Showing is PowerLedger.App.Aero.DashboardView { IsLoaded: true }, TimeSpan.FromSeconds(10), "the Dashboard");
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var roll = (RollingNumber)((PowerLedger.App.Aero.DashboardView)window.PageHost.Showing!).FindName("NowRoll");
                var text = ((SolidColorBrush)window.FindResource("A.B.Text")).Color;
                var digits = MidnightHost.AllOf<TextBlock>(roll).ToList();
                digits.ShouldNotBeEmpty();
                digits.ShouldAllBe(d => ((SolidColorBrush)d.Foreground).Color == text, "Power now's watts in the text colour");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>The mockup's figures are Geist's own proportional digits, and Power now's 146 is tracked in by .035 em
    /// (the .num letter-spacing): each digit's window is its digit's advance plus the tracking, so "1" is narrower than
    /// "4", and the number is as wide as the browser's "146".</summary>
    [Fact]
    public void A_rolling_numbers_digits_are_proportional_and_tracked_as_the_mockups()
        => UiHarness.OnUi(() =>
        {
            var number = new RollingNumber { DigitSize = 48, DigitWeight = FontWeights.SemiBold, Tracking = -0.035, HorizontalAlignment = HorizontalAlignment.Left };
            var host = new Border { Child = number };
            host.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "A.F.Ui");
            host.Resources["A.F.Ui"] = new FontFamily(new Uri("pack://application:,,,/PowerLedger;component/"), "./Fonts/Geist/#Geist");
            number.Set(146, animate: false);
            host.Measure(new Size(400, 200));
            host.Arrange(new Rect(0, 0, 400, 200));
            double Advance(string digit) => new FormattedText(digit, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(System.Windows.Documents.TextElement.GetFontFamily(number), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 48, Brushes.White, 1).WidthIncludingTrailingWhitespace;
            var windows = number.Children.OfType<FrameworkElement>().ToList();
            windows[0].ActualWidth.ShouldBe(Advance("1") - 0.035 * 48, 0.01);
            windows[1].ActualWidth.ShouldBe(Advance("4") - 0.035 * 48, 0.01);
            windows[0].ActualWidth.ShouldBeLessThan(windows[1].ActualWidth, "proportional: the 1 is narrow");
            number.ActualWidth.ShouldBe(Advance("1") + Advance("4") + Advance("6") - 3 * 0.035 * 48, 0.05);
        });

    [Fact]
    public void A_rolling_number_rolls_on_the_spring_when_motion_is_full()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(false);
            var number = new RollingNumber();
            number.Set(3, animate: false);
            number.Set(8);
            ((TranslateTransform)((StackPanel)((Canvas)number.Children[0]).Children[0]).RenderTransform).HasAnimatedProperties.ShouldBeTrue();
            UiHarness.Pump(TimeSpan.FromMilliseconds(AeroMotion.NumberRoll + 200));
            number.Rolled[0].ShouldBe(8, 0.001);
        });

    [Fact]
    public void A_screen_reader_hears_the_number_not_the_strips()
        => UiHarness.OnUi(() =>
        {
            var number = new RollingNumber();
            number.Set(142, animate: false);
            var peer = UIElementAutomationPeer.CreatePeerForElement(number);
            peer.GetName().ShouldBe("142");
            peer.GetChildren().ShouldBeNull();
            AutomationProperties.SetName(number, "Power now in watts");
            peer.GetName().ShouldBe("Power now in watts 142");
        });

    [Theory]
    [InlineData(true, false, true, true, false, true, true)]
    [InlineData(true, true, true, true, false, true, false)]
    [InlineData(false, false, true, true, false, true, false)]
    [InlineData(true, false, false, true, false, true, false)]
    [InlineData(true, false, true, false, false, true, false)]
    [InlineData(true, false, true, true, true, true, false)]
    [InlineData(true, false, true, true, false, false, false)]
    public void The_pulse_breathes_only_while_someone_is_using_the_window(bool breathes, bool reduced, bool visible, bool active, bool minimised, bool awake, bool expected)
        => PulseDot.ShouldBreathe(breathes, reduced, visible, active, minimised, awake).ShouldBe(expected);

    [Fact]
    public void Using_the_window_wakes_the_pulse_and_it_sleeps_again()
        => UiHarness.OnUi(() =>
        {
            var dot = new PulseDot();
            dot.Awake.ShouldBeFalse();
            dot.Wake();
            dot.Awake.ShouldBeTrue();
            dot.Sleep();
            dot.Awake.ShouldBeFalse();
            PulseDot.AwakeFor.ShouldBe(TimeSpan.FromSeconds(15));
        });

    /// <summary>A window in the background (the tests' windows never take the foreground) keeps its pulse still: an idle
    /// App asks for no frames.</summary>
    [Fact]
    public void In_a_background_window_the_pulse_is_still()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(false);
            var window = AeroHost.Dressed(new Window { Width = 100, Height = 100, Left = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None }, Theme.Dark);
            var dot = new PulseDot { Size = 9 };
            window.Content = dot;
            try
            {
                window.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(50));
                window.IsActive.ShouldBeFalse();
                dot.Breathing.ShouldBeFalse();
                ((System.Windows.Shapes.Ellipse)dot.Children[1]).Fill.ShouldBe(window.FindResource("A.B.Accent"));
            }
            finally
            {
                window.Close();
            }
        });

    [Theory]
    [InlineData(50, 0, 0, 1)]
    [InlineData(100, 50, 90, 1)]
    [InlineData(50, 100, 180, 1)]
    [InlineData(0, 50, 270, 1)]
    [InlineData(50, 50, 0, 0)]
    [InlineData(300, 50, 90, 1)]
    public void The_wheel_reads_hue_round_the_edge_and_saturation_out_from_the_centre(double x, double y, double hue, double saturation)
    {
        var (h, s) = ColourWheel.At(new Point(x, y), new Size(100, 100));
        if (saturation > 0) h.ShouldBe(hue, 0.001);
        s.ShouldBe(saturation, 0.001);
        var back = ColourWheel.PointOf(h, s, new Size(100, 100));
        if (x <= 100)
        {
            back.X.ShouldBe(x, 0.001);
            back.Y.ShouldBe(y, 0.001);
        }
    }

    [Theory]
    [InlineData("#7466D8")]
    [InlineData("#FF0000")]
    [InlineData("#2F7552")]
    [InlineData("#FFFFFF")]
    public void A_full_brightness_colour_goes_round_the_wheel_and_back(string hex)
    {
        var colour = (Color)ColorConverter.ConvertFromString(hex);
        var (hue, saturation, value) = ColourWheel.ToHsv(colour);
        var back = ColourWheel.FromHsv(hue, saturation, value);
        Math.Abs(back.R - colour.R).ShouldBeLessThanOrEqualTo(1);
        Math.Abs(back.G - colour.G).ShouldBeLessThanOrEqualTo(1);
        Math.Abs(back.B - colour.B).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void The_keyboard_turns_the_hue_and_the_saturation_and_the_hex_follows()
        => UiHarness.OnUi(() =>
        {
            var (_, wheel) = AeroStylesTests.Dressed(new ColourWheel { Hex = "#FF0000" }, null, Theme.Dark);
            wheel.Hue = 358;
            var changed = 0;
            wheel.ColourChanged += (_, _) => changed++;
            wheel.Hex.ShouldBe("#FF0008");
            wheel.Colour.ShouldBe(Color.FromRgb(0xFF, 0x00, 0x08));
            Press(wheel, Key.Right);
            wheel.Hue.ShouldBe(3, 0.001, "round past red");
            Press(wheel, Key.Left);
            wheel.Hue.ShouldBe(358, 0.001);
            Press(wheel, Key.Up);
            wheel.Saturation.ShouldBe(1, "held at the edge");
            Press(wheel, Key.Down);
            wheel.Saturation.ShouldBe(0.95, 0.001);
            changed.ShouldBe(3, "each change that moved the colour");
            wheel.Hex = "#2F7552";
            wheel.Hue.ShouldBe(ColourWheel.ToHsv(Color.FromRgb(0x2F, 0x75, 0x52)).Hue, 0.001);
            wheel.Colour.ShouldBe(Color.FromRgb(0x2F, 0x75, 0x52), "a set colour is kept exactly");
            wheel.Hex = "not a colour";
            wheel.Colour.ShouldBe(Color.FromRgb(0x2F, 0x75, 0x52), "a bad colour is ignored");
            Press(wheel, Key.Right);
            ColourWheel.ToHsv(wheel.Colour).Value.ShouldBe(0x75 / 255.0, 0.01, "a pick keeps the brightness");
            wheel.Colour = Color.FromRgb(0x08, 0x08, 0x08);
            Press(wheel, Key.Up);
            ColourWheel.ToHsv(wheel.Colour).Value.ShouldBe(ColourWheel.DemoBrightness, 0.01, "from near black a pick takes the demo's brightness");
            wheel.Focusable.ShouldBeTrue();
        });

    [Fact]
    public void A_screen_reader_hears_the_wheel_as_a_colour_slider_and_can_set_it()
        => UiHarness.OnUi(() =>
        {
            var wheel = new ColourWheel { Hex = "#FF0000" };
            var peer = UIElementAutomationPeer.CreatePeerForElement(wheel);
            peer.GetName().ShouldBe("Colour");
            peer.GetAutomationControlType().ShouldBe(AutomationControlType.Slider);
            var value = (IValueProvider)peer.GetPattern(PatternInterface.Value);
            value.Value.ShouldBe("#FF0000, hue 0 degrees, saturation 100 percent");
            value.SetValue("#00ff00");
            wheel.Hue.ShouldBe(120, 0.001);
            wheel.Hex.ShouldBe("#00FF00");
            Should.Throw<ArgumentException>(() => value.SetValue("green"));
        });

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_controls_draw_in_each_theme(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            Directory.CreateDirectory(UiHarness.Folder);
            var window = AeroGlassSample.Window(theme);
            var pane = new GlassPanel { Width = 520, Height = 260, Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var number = new RollingNumber { DigitSize = 40, DigitWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "A.B.Text");
            number.Set(142, animate: false);
            row.Children.Add(number);
            row.Children.Add(new PulseDot { Size = 9, Margin = new Thickness(20, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new ColourWheel { Width = 200, Height = 200, Hex = "#7466D8" });
            pane.Content = row;
            ((Grid)window.Content).Children.Add(pane);
            try
            {
                window.Show();
                window.UpdateLayout();
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                UiHarness.Render(window, AeroGlassSample.Width, AeroGlassSample.Height, $"aero-controls-{themeName.ToLowerInvariant()}.png");
                number.Text.ShouldBe("142");
            }
            finally
            {
                window.Close();
            }
        });

    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("keys"));
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
}
