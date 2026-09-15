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
}
