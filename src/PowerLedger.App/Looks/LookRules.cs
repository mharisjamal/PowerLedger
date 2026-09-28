using System.IO.Packaging;

namespace PowerLedger.App;

/// <summary>What follows from the look chosen (Midnight look design §3, Aero look design §3): which of the six palettes
/// to draw in.</summary>
internal static class LookRules
{
    /// <summary>The palette for a look in a theme. Midnight's define Classic's keys too, and Aero's both Classic's and
    /// Midnight's, so the shared dialogs take the look's colours from the same dictionary. The scheme is taken from
    /// <see cref="PackUriHelper"/>, whose loading registers it: before that, a pack URI won't parse, as in a test with no
    /// window.</summary>
    public static Uri PaletteFor(Look look, Theme theme)
        => new($"{PackUriHelper.UriSchemePack}://application:,,,/PowerLedger;component/Theme/Palette.{Prefix(look)}{theme}.xaml", UriKind.Absolute);

    /// <summary>Classic's palettes are the unprefixed ones, from before there was more than one look.</summary>
    private static string Prefix(Look look) => look switch
    {
        Look.Midnight => "Midnight.",
        Look.Aero => "Aero.",
        _ => "",
    };
}
