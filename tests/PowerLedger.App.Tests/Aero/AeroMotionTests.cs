using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S G1: Aero's motion (the demo's spring and glide as CSS cubic-beziers, durations that reduced motion shortens),
/// the press behaviour, the switch's knob and the capsule radius.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process (agent D's AeroMotionScope)
public class AeroMotionTests
{
    [Fact]
    public void The_curves_are_the_demos_cubic_beziers()
    {
        foreach (var curve in new[] { AeroMotion.Spring, AeroMotion.Glide, AeroMotion.Ease })
        {
            curve.At(0).ShouldBe(0);
            curve.At(1).ShouldBe(1);
        }
        Enumerable.Range(1, 99).Max(i => AeroMotion.Spring.At(i / 100.0)).ShouldBeGreaterThan(1.02, "the spring overshoots, then settles");
        var glide = Enumerable.Range(0, 101).Select(i => AeroMotion.Glide.At(i / 100.0)).ToList();
        glide.Zip(glide.Skip(1)).ShouldAllBe(pair => pair.Second >= pair.First - 1e-9, "the glide never turns back");
        glide.Max().ShouldBeLessThanOrEqualTo(1 + 1e-9);
        AeroMotion.Glide.At(0.5).ShouldBeGreaterThan(0.85, "most of a glide happens early: it settles");
        AeroMotion.Spring.IsFrozen.ShouldBeTrue();
    }

    [Fact]
    public void Reduced_motion_takes_the_travel_out_and_keeps_a_short_fade()
    {
        using (AeroMotion.Force(true))
        {
            AeroMotion.Reduced.ShouldBeTrue();
            AeroMotion.MoveMs(AeroMotion.PaneIn).ShouldBe(0);
            AeroMotion.FadeMs(AeroMotion.Sheen).ShouldBe(AeroMotion.ReducedFade);
            AeroMotion.FadeMs(120).ShouldBe(120);
        }
        using (AeroMotion.Force(false))
        {
            AeroMotion.Reduced.ShouldBeFalse();
            AeroMotion.MoveMs(AeroMotion.PaneIn).ShouldBe(AeroMotion.PaneIn);
        }
        using (AeroMotion.Force(null))
        {
            AeroMotion.Reduced.ShouldBe(!SystemParameters.ClientAreaAnimation, "null follows Windows");
        }
    }

    [Fact]
    public void Changing_the_override_tells_the_listeners_once()
    {
        var heard = 0;
        void Listen() => heard++;
        AeroMotion.Changed += Listen;
        try
        {
            using (AeroMotion.Force(true))
            {
                AeroMotion.SetOverride(true);
            }
            heard.ShouldBe(2, "set, then put back; the same value again says nothing");
        }
        finally
        {
            AeroMotion.Changed -= Listen;
        }
    }

    [Fact]
    public void A_reduced_movement_lands_at_once_and_leaves_no_clock_running()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(true);
            var shift = new TranslateTransform();
            var done = false;
            AeroMotion.Move(shift, TranslateTransform.YProperty, 22, AeroMotion.PaneIn, AeroMotion.Spring, 300, () => done = true, from: 0);
            shift.Y.ShouldBe(22);
            shift.HasAnimatedProperties.ShouldBeFalse();
            done.ShouldBeTrue();
        });

    [Fact]
    public void A_full_movement_runs_on_the_curve_and_ends_where_it_was_sent()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(false);
            var shift = new TranslateTransform();
            var done = false;
            AeroMotion.Move(shift, TranslateTransform.YProperty, 22, 80, AeroMotion.Glide, done: () => done = true, from: 0);
            shift.HasAnimatedProperties.ShouldBeTrue();
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            done.ShouldBeTrue();
            shift.Y.ShouldBe(22);
        });

    [Fact]
    public void The_switchs_knob_travels_across_when_it_turns_on()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(true);
            var (_, glassSwitch) = AeroStylesTests.Dressed(new GlassSwitch(), null, Theme.Dark);
            glassSwitch.KnobOffset.ShouldBe(0);
            glassSwitch.IsChecked = true;
            glassSwitch.KnobOffset.ShouldBe(GlassSwitch.Travel);
            glassSwitch.IsChecked = false;
            glassSwitch.KnobOffset.ShouldBe(0);
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(glassSwitch)
                .ShouldBeOfType<System.Windows.Automation.Peers.ToggleButtonAutomationPeer>();
        });

    /// <summary>Agent S's report: a checked switch whose value arrives after its template (a binding, the usual order)
    /// appears already on, drawn with the knob in place and no animation; only a toggle once it is drawn springs.</summary>
    [Fact]
    public void A_switch_appears_in_place_and_springs_only_when_toggled()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(false);
            var glassSwitch = new GlassSwitch();
            var window = AeroHost.Dressed(new Window { Width = 120, Height = 80, Left = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = glassSwitch }, Theme.Dark);
            try
            {
                glassSwitch.ApplyTemplate();
                glassSwitch.IsChecked = true;
                glassSwitch.KnobOffset.ShouldBe(GlassSwitch.Travel, "in place at once");
                window.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                glassSwitch.KnobOffset.ShouldBe(GlassSwitch.Travel);
                UiHarness.Render(window, 120, 80, "aero-switch-checked-first-show.png");
                glassSwitch.Shown.ShouldBeTrue();

                glassSwitch.IsChecked = false;
                var knob = (TranslateTransform)((FrameworkElement)glassSwitch.Template.FindName("PART_Knob", glassSwitch)).RenderTransform;
                knob.HasAnimatedProperties.ShouldBeTrue("a toggle once drawn springs the knob across");
                UiHarness.Pump(TimeSpan.FromMilliseconds(AeroMotion.Switch + 150));
                glassSwitch.KnobOffset.ShouldBe(0, 0.001);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Press_compresses_a_button_and_springs_it_back()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(true);
            var (_, button) = AeroStylesTests.Dressed(new Button(), "A.GlassBtn", Theme.Dark);
            var scale = Press.ScaleOf(button)!;
            button.RenderTransform.ShouldBeSameAs(scale);
            button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            });
            scale.ScaleX.ShouldBe(AeroMotion.PressScale);
            button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            });
            scale.ScaleX.ShouldBe(1);
            Press.SetEnabled(button, false);
            Press.ScaleOf(button).ShouldBeNull();
        });

    [Theory]
    [InlineData(30, null, 15)]
    [InlineData(48, null, 24)]
    [InlineData(40, 12.0, 12)]
    [InlineData(40, 999.0, 20)]
    public void A_capsules_radius_is_half_its_height_unless_a_real_radius_is_given(double height, double? given, double expected)
    {
        var values = given is { } radius ? new object[] { height, new CornerRadius(radius) } : [height];
        ((CornerRadius)Capsule.Instance.Convert(values, typeof(CornerRadius), null!, CultureInfo.InvariantCulture)).TopLeft.ShouldBe(expected);
    }
}
