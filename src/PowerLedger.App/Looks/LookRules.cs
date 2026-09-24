using System.IO.Packaging;

namespace PowerLedger.App;

/// <summary>What follows from the look chosen (Midnight look design §3): which of the four palettes to draw in.</summary>
internal static class LookRules
{
    /// <summary>The palette for a look in a theme. Midnight's define Classic's keys too, so the shared dialogs take
    /// Midnight's colours from the same dictionary. The scheme is taken from <see cref="PackUriHelper"/>, whose loading
    /// registers it: before that, a pack URI won't parse, as in a test with no window.</summary>
    public static Uri PaletteFor(Look look, Theme theme)
        => new($"{PackUriHelper.UriSchemePack}://application:,,,/PowerLedger;component/Theme/Palette.{(look == Look.Midnight ? "Midnight." : "")}{theme}.xaml",
            UriKind.Absolute);
}
