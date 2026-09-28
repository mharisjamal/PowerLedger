using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PowerLedger.App;

/// <summary>How Aero's glass is tinted (Aero look design §3): barely (Clear), the demo's violet wash (Tinted, the
/// default), black at about 55 % (Dark), or the user's own <see cref="GlassSettings.TintColor"/> (Colour).</summary>
internal enum GlassStyle
{
    Clear,
    Tinted,
    Dark,
    Colour,
}

/// <summary>Aero's accent (Aero look design §3): the demo's lime, the default, or one of four others.</summary>
internal enum GlassAccent
{
    Lime,
    Ice,
    Indigo,
    Amber,
    Rose,
}

/// <summary>What shows through Aero's glass (Aero look design §3): the desktop itself through the system backdrop, the
/// user's wallpaper frosted once, or a plain ground.</summary>
internal enum GlassBackdrop
{
    Desktop,
    Wallpaper,
    Plain,
}

/// <summary>Where the watts overlay sits (Aero look design §5): a corner of the work area, or where it was dragged.</summary>
internal enum OverlayPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Free,
}

/// <summary>
/// Aero's Settings, Glass section (Aero look design §3), kept in ui.json as <see cref="UiPreferences.Glass"/>. Every
/// property has a setter rather than init, as <see cref="UiPreferences"/>'s defaulted ones do: the JSON source generator
/// gives an init-only property missing from the file its type's default, where a setter is left alone, so a ui.json
/// written before a field existed keeps that field's default. Anything out of range is put back by
/// <see cref="Sanitised"/>, which the store runs on every load.
/// </summary>
internal sealed partial record GlassSettings
{
    /// <summary>The demo's violet, the tint a Colour style starts from.</summary>
    public const string DefaultTint = "#7466D8";

    /// <summary>A grid at or above this many grams of CO₂ per kWh is not a real grid (<see cref="UiPreferences.MaxCo2KgPerKwh"/>).</summary>
    public const double MaxCarbonGramsPerKwh = UiPreferences.MaxCo2KgPerKwh * 1000;

    [JsonConverter(typeof(GlassStyleJsonConverter))]
    public GlassStyle Style { get; set; } = GlassStyle.Tinted;

    /// <summary>The Colour style's tint as #RRGGBB, upper-case; anything else reads as <see cref="DefaultTint"/>.</summary>
    public string TintColor { get; set; } = DefaultTint;

    /// <summary>How strongly the tint shows, 0 to 1.</summary>
    public double TintStrength { get; set; } = 0.5;

    /// <summary>How much the glass blurs what is behind it, 0 to 1 (the Frost slider).</summary>
    public double Frost { get; set; } = 0.6;

    /// <summary>How bright the glass's rim is, 0 to 1 (the Edge light slider).</summary>
    public double EdgeLight { get; set; } = 0.6;

    [JsonConverter(typeof(GlassAccentJsonConverter))]
    public GlassAccent Accent { get; set; } = GlassAccent.Lime;

    [JsonConverter(typeof(GlassBackdropJsonConverter))]
    public GlassBackdrop Backdrop { get; set; } = GlassBackdrop.Desktop;

    /// <summary>An opaque frosted fill instead of see-through glass.</summary>
    public bool ReduceTransparency { get; set; }

    /// <summary>Solid rims, full-strength text and a darker wash.</summary>
    public bool IncreaseContrast { get; set; }

    /// <summary>Reduce motion as chosen here; null follows Windows (<c>SystemParameters.ClientAreaAnimation</c>) until the
    /// user changes it.</summary>
    public bool? ReduceMotion { get; set; }

    /// <summary>Tilt and parallax on the panels as the pointer moves; off under reduced motion whatever this says.</summary>
    public bool Parallax { get; set; } = true;

    /// <summary>The Carbon insight's grid factor in grams of CO₂ per kWh as the user set it; null takes the built-in
    /// table's figure for Windows' region (Aero look design §4).</summary>
    public double? CarbonGramsPerKwh { get; set; }

    public static GlassSettings Default { get; } = new();

    /// <summary>The same settings with a name this version doesn't know put back to its default, the tint colour
    /// normalised to upper-case #RRGGBB or put back, the sliders clamped to 0 to 1 (a number that isn't one put back),
    /// and a carbon factor no grid has read as by region.</summary>
    public GlassSettings Sanitised() => this with
    {
        Style = Enum.IsDefined(Style) ? Style : Default.Style,
        TintColor = TintColor is { } tint && HexColour().IsMatch(tint) ? tint.ToUpperInvariant() : DefaultTint,
        TintStrength = Fraction(TintStrength, Default.TintStrength),
        Frost = Fraction(Frost, Default.Frost),
        EdgeLight = Fraction(EdgeLight, Default.EdgeLight),
        Accent = Enum.IsDefined(Accent) ? Accent : Default.Accent,
        Backdrop = Enum.IsDefined(Backdrop) ? Backdrop : Default.Backdrop,
        CarbonGramsPerKwh = CarbonGramsPerKwh is >= 0 and < MaxCarbonGramsPerKwh ? CarbonGramsPerKwh : null,
    };

    /// <summary><paramref name="value"/> clamped to 0 to 1; <paramref name="fallback"/> for a value that is no number.</summary>
    internal static double Fraction(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}

/// <summary>
/// The watts overlay's choices (Aero look design §5), kept in ui.json as <see cref="UiPreferences.Overlay"/>, with setters
/// for the same reason as <see cref="GlassSettings"/>'s. <see cref="Left"/> and <see cref="Top"/> are where a Free overlay
/// was last dragged to, in device-independent pixels on the virtual screen (so negative on a monitor left of the main
/// one); null until it has been dragged. The overlay clamps them to a work area as it shows, not here: the monitors may
/// have changed since.
/// </summary>
internal sealed record OverlaySettings
{
    /// <summary>The lowest opacity the overlay's slider offers: below it the watts stop reading on a busy desktop.</summary>
    public const double MinOpacity = 0.55;

    public bool Enabled { get; set; }

    [JsonConverter(typeof(OverlayPositionJsonConverter))]
    public OverlayPosition Position { get; set; } = OverlayPosition.TopRight;

    public double? Left { get; set; }

    public double? Top { get; set; }

    /// <summary>The whole pill's opacity, <see cref="MinOpacity"/> to 1.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>The 30 s sparkline under the watts.</summary>
    public bool Sparkline { get; set; } = true;

    public static OverlaySettings Default { get; } = new();

    /// <summary>The same settings with an unknown position put back to the default, the opacity clamped to its range (a
    /// value that is no number put back), and a place that is no number forgotten.</summary>
    public OverlaySettings Sanitised() => this with
    {
        Position = Enum.IsDefined(Position) ? Position : Default.Position,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1) : Default.Opacity,
        Left = Left is { } left && double.IsFinite(left) ? left : null,
        Top = Top is { } top && double.IsFinite(top) ? top : null,
    };
}

/// <summary>An enum by its name, matched without regard to case, and anything else (a name this version doesn't know, a
/// number, null, a list of names) as <paramref name="fallback"/>, so ui.json still loads whole: the string-enum converter
/// would refuse the file instead. The same rule as <see cref="LookJsonConverter"/> and <see cref="EnergyPeriodJsonConverter"/>,
/// for the Glass and Overlay settings' enums.</summary>
internal abstract class NamedEnumJsonConverter<T>(T fallback) : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) return fallback;
        var name = reader.GetString();
        foreach (var value in Enum.GetValues<T>())
        {
            if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal sealed class GlassStyleJsonConverter() : NamedEnumJsonConverter<GlassStyle>(GlassSettings.Default.Style);

internal sealed class GlassAccentJsonConverter() : NamedEnumJsonConverter<GlassAccent>(GlassSettings.Default.Accent);

internal sealed class GlassBackdropJsonConverter() : NamedEnumJsonConverter<GlassBackdrop>(GlassSettings.Default.Backdrop);

internal sealed class OverlayPositionJsonConverter() : NamedEnumJsonConverter<OverlayPosition>(OverlaySettings.Default.Position);
