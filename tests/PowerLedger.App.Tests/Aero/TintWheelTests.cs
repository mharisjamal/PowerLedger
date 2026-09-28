using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §3: the Colour style's hue and saturation wheel. Hue runs clockwise from red at the top,
/// saturation out from the white centre, and the colour's brightness is kept.</summary>
public class TintWheelTests
{
    [Fact]
    public void The_centre_is_white_and_the_rim_is_the_full_hue()
    {
        TintWheel.At(new Point(100, 100), 200, 1).ShouldBe(Colors.White);
        TintWheel.At(new Point(100, 0), 200, 1).ShouldBe(Color.FromRgb(255, 0, 0), "red at the top");
        TintWheel.At(new Point(200, 100), 200, 1).ShouldBe(Color.FromRgb(128, 255, 0), "a quarter round, clockwise: 90 degrees");
        TintWheel.At(new Point(100, 200), 200, 1).ShouldBe(Color.FromRgb(0, 255, 255), "cyan at the foot");
    }

    [Fact]
    public void A_point_past_the_rim_takes_the_rims_colour()
        => TintWheel.At(new Point(100, -50), 200, 1).ShouldBe(Color.FromRgb(255, 0, 0));

    [Fact]
    public void The_brightness_is_kept()
        => TintWheel.At(new Point(100, 0), 200, 0.5).ShouldBe(Color.FromRgb(128, 0, 0));

    [Theory]
    [InlineData("#7466D8")]
    [InlineData("#3A7BD5")]
    [InlineData("#D08B2C")]
    [InlineData("#6B7280")]
    public void A_colour_is_found_where_the_wheel_would_pick_it(string hex)
    {
        var colour = (Color)ColorConverter.ConvertFromString(hex);
        var value = TintWheel.ToHsv(colour).Value;

        var back = TintWheel.At(TintWheel.Where(colour, 400), 400, value);

        Math.Abs(back.R - colour.R).ShouldBeLessThanOrEqualTo(2);
        Math.Abs(back.G - colour.G).ShouldBeLessThanOrEqualTo(2);
        Math.Abs(back.B - colour.B).ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    [Trait("Category", "UI")]
    public void The_arrow_keys_turn_the_hue_and_change_the_saturation()
        => UiHarness.OnUi(() =>
        {
            var wheel = new TintWheel { Colour = TintWheel.FromHsv(100, 0.5, 0.8) };

            Press(wheel, Key.Right);
            Press(wheel, Key.Up);

            var (hue, saturation, value) = TintWheel.ToHsv(wheel.Colour);
            hue.ShouldBe(105, 1.5);
            saturation.ShouldBe(0.55, 0.01);
            value.ShouldBe(0.8, 0.01);
        });

    private static void Press(UIElement element, Key key)
    {
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.KeyDownEvent });
    }
}
