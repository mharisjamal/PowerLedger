using System.Windows;

namespace PowerLedger.App.Tests;

/// <summary>
/// TEMPORARY (plan O M1): the Midnight palettes from the copies beside this file, until plan-o/f lands
/// Theme/Palette.Midnight.*.xaml and LookRules.PaletteFor; then this class goes and the tests ask the App.
/// </summary>
internal static class TempPalette
{
    public static ResourceDictionary Load(Theme theme)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.ResourceAssembly;
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PowerLedger.App.Tests;component/Midnight/Temp/Palette.Midnight.{theme}.xaml", UriKind.Absolute),
        };
    }
}
