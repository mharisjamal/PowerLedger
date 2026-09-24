using Shouldly;

namespace PowerLedger.App.Tests;

public class LookRulesTests
{
    [Theory]
    [InlineData("Classic", "Dark", "Palette.Dark.xaml")]
    [InlineData("Classic", "Light", "Palette.Light.xaml")]
    [InlineData("Midnight", "Dark", "Palette.Midnight.Dark.xaml")]
    [InlineData("Midnight", "Light", "Palette.Midnight.Light.xaml")]
    public void Each_look_draws_each_theme_from_its_own_palette(string look, string theme, string file)
        => LookRules.PaletteFor(Enum.Parse<Look>(look), Enum.Parse<Theme>(theme))
            .ToString().ShouldBe($"pack://application:,,,/PowerLedger;component/Theme/{file}");
}
