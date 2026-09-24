using System.Windows;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan O 0.5: every Midnight animation takes its length from Motion, which is zero under reduced motion except a 120 ms fade.
/// The collection keeps every test that forces the setting off the others' thread.</summary>
[Collection("Motion")]
public class MotionTests
{
    [Fact]
    public void Under_reduced_motion_movement_takes_no_time_and_a_fade_keeps_120_ms()
    {
        using (Motion.Force(reduced: true))
        {
            Motion.Reduced.ShouldBeTrue();
            Motion.Of(Motion.Base).ShouldBe(new Duration(TimeSpan.Zero));
            Motion.Of(Motion.Slow).ShouldBe(new Duration(TimeSpan.Zero));
            Motion.Fade(Motion.Base).ShouldBe(new Duration(TimeSpan.FromMilliseconds(120)));
            Motion.Fade(Motion.Fast).ShouldBe(new Duration(TimeSpan.FromMilliseconds(120)));
        }
    }

    [Fact]
    public void With_motion_on_every_length_is_its_own()
    {
        using (Motion.Force(reduced: false))
        {
            Motion.Reduced.ShouldBeFalse();
            Motion.Of(Motion.Base).ShouldBe(Motion.Base);
            Motion.Fade(Motion.Fast).ShouldBe(Motion.Fast);
        }
    }

    [Fact]
    public void The_forced_setting_ends_with_its_scope()
    {
        using (Motion.Force(reduced: true)) Motion.Reduced.ShouldBeTrue();
        Motion.Reduced.ShouldBe(!SystemParameters.ClientAreaAnimation);
    }

    [Fact]
    public void The_markup_extension_hands_the_styles_the_same_lengths()
    {
        using (Motion.Force(reduced: true))
        {
            ((Duration)new MotionExtension(MotionSpeed.Base).ProvideValue(null!)).ShouldBe(new Duration(TimeSpan.Zero));
            ((Duration)new MotionExtension(MotionSpeed.Fast) { Fade = true }.ProvideValue(null!)).ShouldBe(new Duration(TimeSpan.FromMilliseconds(120)));
        }
        using (Motion.Force(reduced: false))
        {
            ((Duration)new MotionExtension(MotionSpeed.Fast).ProvideValue(null!)).ShouldBe(Motion.Fast);
            ((Duration)new MotionExtension(MotionSpeed.Slow).ProvideValue(null!)).ShouldBe(Motion.Slow);
        }
    }
}
