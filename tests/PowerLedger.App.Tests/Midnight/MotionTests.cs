using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
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

    /// <summary>Review 6: a style's storyboard is frozen when the style loads, so a length fixed then would ignore Windows'
    /// setting changed later in the session. A MotionAnimation asks Motion each time it starts.</summary>
    [Fact]
    [Trait("Category", "UI")]
    public void A_styles_animation_asks_motion_each_time_it_starts()
        => UiHarness.OnUi(() =>
        {
            var styles = MidnightStylesTests.Load();
            var lift = ((Style)styles["M.Card"]).Triggers.OfType<Trigger>().Single().EnterActions.OfType<BeginStoryboard>().Single()
                .Storyboard.Children.OfType<MotionAnimation>().Single();
            using (Motion.Force(reduced: false)) lift.CreateClock().NaturalDuration.ShouldBe(Motion.Fast);
            using (Motion.Force(reduced: true)) lift.CreateClock().NaturalDuration.ShouldBe(new Duration(TimeSpan.Zero), "the same frozen animation, the setting now");
            using (Motion.Force(reduced: false)) new MotionAnimation { Speed = MotionSpeed.Slow }.CreateClock().NaturalDuration.ShouldBe(Motion.Slow);
            using (Motion.Force(reduced: true)) new MotionAnimation { Speed = MotionSpeed.Fast, Fade = true }.CreateClock().NaturalDuration
                .ShouldBe(new Duration(TimeSpan.FromMilliseconds(120)), "a fade keeps 120 ms");

            var animations = Animations(styles).ToList();
            animations.Count.ShouldBeGreaterThan(10);
            animations.ShouldAllBe(animation => animation is MotionAnimation && animation.Duration == Duration.Automatic, "every one of the sheet's asks Motion");
        });

    /// <summary>Every animation in the sheet's styles and templates: their triggers' storyboards.</summary>
    private static IEnumerable<Timeline> Animations(ResourceDictionary styles)
    {
        static IEnumerable<TriggerAction> Actions(TriggerBase trigger) => trigger switch
        {
            EventTrigger events => events.Actions,
            _ => trigger.EnterActions.Concat(trigger.ExitActions),
        };
        static IEnumerable<Timeline> From(IEnumerable<TriggerBase> triggers)
            => triggers.SelectMany(Actions).OfType<BeginStoryboard>().SelectMany(begin => begin.Storyboard.Children);
        foreach (var value in styles.Values)
        {
            IEnumerable<TriggerBase> triggers = value is Style style ? style.Triggers : [];
            var template = value as ControlTemplate
                ?? (value as Style)?.Setters.OfType<Setter>().Select(setter => setter.Value).OfType<ControlTemplate>().FirstOrDefault();
            foreach (var animation in From(triggers)) yield return animation;
            if (template is not null) foreach (var animation in From(template.Triggers)) yield return animation;
        }
    }
}
