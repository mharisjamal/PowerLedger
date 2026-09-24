using System.Windows;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ThemeRulesTests
{
    [Theory]
    [InlineData("System", false, "Dark")]
    [InlineData("System", true, "Light")]
    [InlineData("Dark", true, "Dark")]
    [InlineData("Light", false, "Light")]
    public void The_choice_wins_and_system_follows_windows(string choice, bool windowsUsesLight, string expected)
        => ThemeRules.Resolve(Enum.Parse<ThemeChoice>(choice), windowsUsesLight).ToString().ShouldBe(expected);

    /// <summary>The manager keeps exactly one palette, first among the application's dictionaries, and swaps it for the
    /// look as for the theme: a switch of look keeps the choice, and a new choice keeps the look.</summary>
    [Fact]
    [Trait("Category", "UI")]
    public void One_palette_stays_first_and_follows_the_look_and_the_choice()
        => UiHarness.OnUi(() =>
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            var before = merged.Count;
            using var manager = new ThemeManager(Application.Current, ThemeChoice.Dark);
            try
            {
                manager.Current.ShouldBe(Theme.Dark);
                manager.Look.ShouldBe(Look.Classic);
                merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Classic, Theme.Dark));

                manager.Apply(Look.Midnight);
                merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Midnight, Theme.Dark));
                manager.Current.ShouldBe(Theme.Dark);

                manager.Choose(ThemeChoice.Light);
                merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Midnight, Theme.Light));
                manager.Look.ShouldBe(Look.Midnight);

                manager.Apply(Look.Classic);
                merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Classic, Theme.Light));
                merged.Count.ShouldBe(before + 1);
            }
            finally
            {
                merged.RemoveAt(0);   // the harness keeps its own palette; the manager's must not shadow it
            }
        });
}
