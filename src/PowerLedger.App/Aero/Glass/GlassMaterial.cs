using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PowerLedger.App.Aero;

/// <summary>
/// Turns the Glass settings into the glass (Aero look design §3, Plan S G3): the A.* tokens a window's panes, text and
/// controls draw with, put on that window's resources (never Application's) in a dictionary of their own, so every
/// DynamicResource repaints at once and nothing is rebuilt. It listens to <see cref="SettingsViewModel"/>'s one live
/// channel, <c>PropertyChanged</c> for Glass (and Theme), and to Windows' colour settings.
/// <list type="bullet">
/// <item>Style: Clear a faint tint; Tinted the demo's; Dark black at 55 %; Colour the user's hue. Tint strength scales
/// each around its default (0.5 is the demo).</item>
/// <item>Frost scales the frost blur (<c>A.Glass.Frost</c>, 26 at 0.6, which WallpaperFrost reads); Edge light the rim
/// and sheens (0.6 is the demo's 75 %).</item>
/// <item>Reduce transparency: an opaque frosted fill for panes, menus and dialogs. Increase contrast: solid rims, every
/// text step at full strength, a darker wash under see-through glass.</item>
/// <item>Accent: the lime or one of four, each with its ink, carried to the shared keys (Midnight's accent, focus, the CPU
/// part and the charts) so the dialogs and the pie follow it.</item>
/// <item>Reduce motion: AeroMotion's override (null follows Windows).</item>
/// </list>
/// Whatever the style, the ink family (light text on dark glass or dark on light) is the one that reads better on the
/// glass over the palette's darkest and brightest backdrop, and the quieter text steps are raised where a tint would take
/// them under 4.5:1: adaptive contrast, so no setting leaves text unreadable.
/// </summary>
internal sealed class GlassMaterial : IDisposable
{
    /// <summary>The demo's defaults, where each slider sits for the look as approved.</summary>
    public const double DemoTintStrength = 0.5;
    public const double DemoEdgeLight = 0.6;
    public const double DemoFrost = 0.6;

    /// <summary>The Dark style's black, at the tint's top and its foot (the HTML's 55 %).</summary>
    public const double DarkTop = 0.55;
    public const double DarkBottom = 0.45;

    /// <summary>The Clear style's tint, as a share of the demo's.</summary>
    public const double ClearShare = 0.35;

    /// <summary>The Colour style's tint at the default strength: the hue at 30 % over the scene.</summary>
    public const double ColourAlpha = 0.3;

    [ThreadStatic]
    private static Dictionary<Theme, ResourceDictionary>? _palettes;

    /// <summary>The colour keys that follow the ink family rather than the theme: light-on-dark or dark-on-light.</summary>
    private static readonly string[] Family =
    [
        "A.C.Text", "A.C.Text2", "A.C.Text3", "A.C.Well", "A.C.Well2", "A.C.Well3", "A.C.WellHover", "A.C.WellEdge", "A.C.WellTop", "A.C.Line",
        "A.C.ChartWell", "A.C.ChartWellTop", "A.C.Grid", "A.C.Track", "A.C.TrackSoft", "A.C.Muted", "A.C.Ghost", "A.C.PrevLine", "A.C.Guide",
        "A.C.GuideStrong", "A.C.BtnFill", "A.C.BtnFillHover", "A.C.BtnEdge", "A.C.BtnTop", "A.C.OutlineHover", "A.C.NavFillA", "A.C.NavFillB",
        "A.C.NavHover", "A.C.NavIcon", "A.C.NavIndicator", "A.C.NavIndicatorTop", "A.C.SwitchOff", "A.C.MenuHover", "A.C.RimA", "A.C.RimB",
        "A.C.RimC", "A.C.RimD", "A.C.RimInner", "A.C.RimDark", "A.C.TopSheen", "A.C.PointerSheen", "A.C.Ink", "A.C.Pill", "A.C.MenuFill",
        "A.C.MenuEdge", "A.C.ModalTop", "A.C.ModalBottom", "A.C.Halo",
    ];

    /// <summary>The single-colour brushes the palette builds from a token, rebuilt here when their token changes.</summary>
    private static readonly string[] Brushed =
    [
        "Text", "Text2", "Text3", "Ink", "Accent", "AccentInk", "Pill", "Well", "Well2", "Well3", "WellHover", "WellEdge", "WellTop", "Line",
        "ChartWell", "ChartWellTop", "Track", "TrackSoft", "Muted", "BtnFill", "BtnFillHover", "BtnEdge", "BtnTop", "OutlineHover", "AccentTop",
        "AccentHover", "NavHover", "NavIcon", "NavIndicator", "NavIndicatorTop", "SwitchOff", "MenuFill", "MenuEdge", "MenuHover", "Scrim",
        "RimInner", "RimDark", "SeeThroughWash",
    ];

    private readonly FrameworkElement _target;
    private readonly Func<GlassSettings> _read;
    private readonly Func<Theme> _theme;
    private readonly INotifyPropertyChanged? _source;
    private readonly ResourceDictionary _applied = [];
    private bool _disposed;

    /// <param name="target">The window (or, in a test, the element) whose resources take the glass.</param>
    /// <param name="read">The settings in force: <c>SettingsViewModel.Glass</c>.</param>
    /// <param name="theme">The theme in force: <c>ThemeManager.Current</c>.</param>
    /// <param name="source">What raises Glass and Theme: the SettingsViewModel; null for a fixed sample.</param>
    public GlassMaterial(FrameworkElement target, Func<GlassSettings> read, Func<Theme> theme, INotifyPropertyChanged? source = null)
    {
        _target = target;
        _read = read;
        _theme = theme;
        _source = source;
        target.Resources.MergedDictionaries.Add(_applied);
        if (source != null) source.PropertyChanged += OnSettingChanged;
        SystemEvents.UserPreferenceChanged += OnWindowsChanged;
        Refresh();
    }

    /// <summary>Raised after the glass was repainted from new settings, for the backdrop and the parallax.</summary>
    public event Action<GlassSettings>? Changed;

    public GlassSettings Current { get; private set; } = GlassSettings.Default;

    /// <summary>Aero's material on a window, live from Settings and the theme manager.</summary>
    public static GlassMaterial For(FrameworkElement window, SettingsViewModel settings, ThemeManager theme)
        => new(window, () => settings.Glass, () => theme.Current, settings);

    /// <summary>Repaints from the settings and the theme in force now.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        Current = _read();
        AeroMotion.SetOverride(Current.ReduceMotion);
        var theme = _theme();
        var map = Map(Current, theme);
        foreach (var (key, value) in map)
        {
            if (!_applied.Contains(key) || !Same(_applied[key], value)) _applied[key] = value;
        }
        var halo = map[HaloOnKey] is true ? Halo(Current, theme, (Color)map["A.C.Halo"]) : null;
        if (!_applied.Contains(HaloKey) || !Same(_applied[HaloKey], halo)) _applied[HaloKey] = halo;
        Changed?.Invoke(Current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source != null) _source.PropertyChanged -= OnSettingChanged;
        SystemEvents.UserPreferenceChanged -= OnWindowsChanged;
        _target.Resources.MergedDictionaries.Remove(_applied);
    }

    /// <summary>
    /// The tokens <paramref name="settings"/> give in <paramref name="theme"/>: every A.* colour, brush and number the
    /// glass draws with, and the shared keys the accent reaches. At the demo's settings each equals the palette's own.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Map(GlassSettings settings, Theme theme)
    {
        settings = settings.Sanitised();
        var palette = Palette(theme);
        var colours = new Dictionary<string, Color>();
        foreach (var key in palette.Keys.OfType<string>())
        {
            if (key.StartsWith("A.C.", StringComparison.Ordinal) && palette[key] is Color colour) colours[key] = colour;
        }
        var darkest = colours["A.C.BackdropDarkest"];
        var brightest = colours["A.C.BackdropBrightest"];

        // Bright glass (0.10.1, the owner's choice, as the demo): the frost lets the desktop through up to A.C.FrostDarkest
        // and A.C.FrostBrightest, and text keeps 3:1 there with the soft halo behind it (A.Glass.Halo, counted as its colour
        // at A.Glass.HaloShare). Increase contrast and Reduce transparency keep the strict glass: the frost held within
        // the backdrop bounds, no halo, and 4.5:1.
        // Aero bloom (0.10.3): the frost is the bloom itself, whole, so its grounds are the frosted bloom's darkest and
        // brightest under the stage (A.C.BloomDarkest and A.C.BloomBrightest), and the halo stays only where text needs it
        // there for 3:1.
        var strict = Strict(settings);
        var bloom = settings.Backdrop == GlassBackdrop.Bloom;
        var target = strict ? StrictContrast : GlassContrast;
        var low = strict ? darkest : colours[bloom ? "A.C.BloomDarkest" : "A.C.FrostDarkest"];
        var high = strict ? brightest : colours[bloom ? "A.C.BloomBrightest" : "A.C.FrostBrightest"];
        var share = strict ? 0 : (double)palette["A.Glass.HaloShare"];
        Color[] GroundsOf(Color tintTop, Color tintBottom, Color halo)
            => new[] { low, high }.SelectMany(b => new[] { Contrast.Over(tintTop, b), Contrast.Over(tintBottom, b) })
                .Select(g => Contrast.Over(WithAlpha(halo, share), g)).ToArray();

        // The tint.
        var (top, bottom) = Tint(settings, colours);
        if (settings.ReduceTransparency)
        {
            // Opaque: the tint laid over the frosted tone a dark or light scene averages to.
            var frosted = Mix(darkest, brightest, 0.5);
            top = Contrast.Over(top, frosted);
            bottom = Contrast.Over(bottom, frosted);
        }
        // The theme's own ink family while it reads on this glass; otherwise the family that reads better, each with its
        // own halo (the dark glass's floor, Plan V, can put dark ink within reach of light ink there: the theme keeps its own).
        var light = Palette(Theme.Dark);
        var dark = Palette(Theme.Light);
        var lightInk = (Color)light["A.C.Text"];
        var darkInk = (Color)dark["A.C.Text"];
        var lightWorst = Worst(lightInk, GroundsOf(top, bottom, (Color)light["A.C.Halo"]));
        var darkWorst = Worst(darkInk, GroundsOf(top, bottom, (Color)dark["A.C.Halo"]));
        var ownWorst = ReferenceEquals(palette, light) ? lightWorst : darkWorst;
        var family = ownWorst >= target ? palette : lightWorst >= darkWorst ? light : dark;
        var inkColour = ReferenceEquals(family, light) ? lightInk : darkInk;
        var haloColour = (Color)family["A.C.Halo"];
        var haloOn = !strict;
        if (bloom && !strict)
        {
            var withHalo = share;
            share = 0;
            if (Worst(inkColour, GroundsOf(top, bottom, haloColour)) >= target) haloOn = false;
            else share = withHalo;
        }
        var grounds = GroundsOf(top, bottom, haloColour);
        if (Worst(inkColour, grounds) < target)
        {
            // Neither ink reads on this tint over both backdrops (black at 55 % over a light scene, a bright hue): the
            // tint is drawn toward the pole the ink reads on, and made denser, until it does.
            var pole = ReferenceEquals(family, light) ? Colors.Black : Colors.White;
            top = Toward(top, pole, inkColour, tint => GroundsOf(tint, tint, haloColour), target);
            bottom = Toward(bottom, pole, inkColour, tint => GroundsOf(tint, tint, haloColour), target);
            grounds = GroundsOf(top, bottom, haloColour);
        }
        colours["A.C.GlassTintTop"] = top;
        colours["A.C.GlassTintBottom"] = bottom;
        if (!ReferenceEquals(family, palette))
        {
            foreach (var key in Family) colours[key] = (Color)family[key];
        }

        // The rim and sheens: Edge light scales them around the demo's; Increase contrast makes the rim solid.
        var edge = settings.EdgeLight / DemoEdgeLight;
        foreach (var key in new[] { "A.C.RimA", "A.C.RimB", "A.C.RimC", "A.C.RimD", "A.C.RimInner", "A.C.TopSheen", "A.C.PointerSheen" })
            colours[key] = Scaled(colours[key], edge);
        if (settings.IncreaseContrast)
        {
            var solid = WithAlpha(colours["A.C.RimA"], Math.Max(colours["A.C.RimA"].A / 255.0, 0.8));
            foreach (var key in new[] { "A.C.RimA", "A.C.RimB", "A.C.RimC", "A.C.RimD" }) colours[key] = solid;
            colours["A.C.SeeThroughWash"] = Scaled(colours["A.C.SeeThroughWash"], 2);
        }

        // The wells text sits in: on the bright glass, a light well over a light frost is let back toward the glass until
        // the ink reads on it (the glass itself reads).
        var text = colours["A.C.Text"];
        if (!strict)
        {
            foreach (var key in new[] { "A.C.Well", "A.C.Well2", "A.C.Well3", "A.C.WellHover", "A.C.MenuHover", "A.C.ChartWell" })
                colours[key] = Quieter(colours[key], text, grounds, target);
        }

        // Text: full strength under Increase contrast; otherwise each quieter step raised where this glass needs it.
        if (settings.IncreaseContrast)
        {
            colours["A.C.Text2"] = colours["A.C.Text3"] = text;
        }
        else
        {
            var chart = grounds.Select(g => Contrast.Over(colours["A.C.ChartWell"], g));
            var quiet = grounds.Concat(new[] { "A.C.Well", "A.C.WellHover", "A.C.MenuHover" }.SelectMany(w => grounds.Select(g => Contrast.Over(colours[w], g)))).ToArray();
            if (!strict)
            {
                // A top-bar pill on the bright glass: the frost and the halo under its button's light fill, at rest and
                // under the pointer (the household's second line is Text2).
                var pills = new[] { low, high }.Select(b => Contrast.Over(colours["A.C.BtnFill"], Contrast.Over(WithAlpha(haloColour, share), b)));
                quiet = quiet.Concat(pills).Concat(pills.Select(g => Contrast.Over(colours["A.C.OutlineHover"], g))).ToArray();
            }
            colours["A.C.Text2"] = Readable(colours["A.C.Text2"], quiet, target);
            colours["A.C.Text3"] = Readable(colours["A.C.Text3"], grounds.Concat(chart).ToArray(), target);
        }

        // Menus and dialogs: opaque under Reduce transparency; otherwise as dense as their text needs over either backdrop.
        foreach (var key in new[] { "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom" })
        {
            colours[key] = settings.ReduceTransparency ? WithAlpha(colours[key], 1) : Denser(colours[key], colours["A.C.Text2"], darkest, brightest);
        }

        // The accent.
        var accentName = settings.Accent.ToString();
        var accent = colours["A.C.Accent." + accentName];
        var ink = colours["A.C.AccentInk." + accentName];
        colours["A.C.Accent"] = accent;
        colours["A.C.AccentInk"] = ink;

        var result = new Dictionary<string, object>();
        foreach (var (key, colour) in colours) result[key] = colour;
        foreach (var name in Brushed) result["A.B." + name] = Solid(colours["A.C." + name]);
        result["A.B.GlassTint"] = Vertical(top, bottom);
        result["A.B.Rim"] = Rim(colours);
        result["A.B.TopSheen"] = TopSheen(colours["A.C.TopSheen"]);
        result["A.B.NavFill"] = Frozen(new LinearGradientBrush(colours["A.C.NavFillA"], colours["A.C.NavFillB"], new Point(0, 0), new Point(1, 0)));
        result["A.B.ModalFill"] = Vertical(colours["A.C.ModalTop"], colours["A.C.ModalBottom"]);
        // A top-bar pill: the bright glass's frost under its button's own light fill, as the demo; strict, the dialogs' wash.
        result["A.B.PillFill"] = strict ? result["A.B.ModalFill"] : Solid(Colors.Transparent);

        result["A.Glass.Frost"] = (double)palette["A.Glass.Frost"] * settings.Frost / DemoFrost;
        result[HaloOnKey] = haloOn;

        // The shared keys the accent reaches: Midnight's and Classic's accent, focus, CPU part and charts, so the dialogs
        // and the pie follow the chosen accent.
        var soft = WithAlpha(accent, ((SolidColorBrush)palette["M.AccentSoft"]).Color.A / 255.0);
        foreach (var key in new[] { "M.Accent", "M.Focus", "Brush.Amber", "M.PartCpu", "Brush.PartCpu", "M.ChartLine", "M.BarTop" })
            result[key] = Solid(accent);
        foreach (var key in new[] { "M.OnAccent", "Brush.OnAmber" }) result[key] = Solid(ink);
        foreach (var key in new[] { "M.AccentSoft", "Brush.AmberSoft" }) result[key] = Solid(soft);
        result["M.BarBottom"] = Solid(Shade(accent, 0.75));
        result["M.ChartFillTop"] = Solid(WithAlpha(accent, ((SolidColorBrush)palette["M.ChartFillTop"]).Color.A / 255.0));
        result["M.ChartFillBottom"] = Solid(WithAlpha(accent, 0));
        if (settings.Accent != GlassAccent.Lime && theme == Theme.Dark) result["M.AccentText"] = Solid(accent);
        return result;
    }

    /// <summary>A palette by theme, loaded once on this thread.</summary>
    internal static ResourceDictionary Palette(Theme theme)
    {
        _palettes ??= [];
        if (!_palettes.TryGetValue(theme, out var palette)) _palettes[theme] = palette = ThemeManager.Palette(Look.Aero, theme);
        return palette;
    }

    /// <summary>The tint's top and foot for the style, scaled by strength around the demo's.</summary>
    private static (Color Top, Color Bottom) Tint(GlassSettings settings, Dictionary<string, Color> colours)
    {
        var strength = Strength(settings.TintStrength);
        var top = colours["A.C.GlassTintTop"];
        var bottom = colours["A.C.GlassTintBottom"];
        return settings.Style switch
        {
            GlassStyle.Clear => (Scaled(top, strength * ClearShare), Scaled(bottom, strength * ClearShare)),
            GlassStyle.Dark => (WithAlpha(Colors.Black, Math.Min(0.85, DarkTop * strength)), WithAlpha(Colors.Black, Math.Min(0.8, DarkBottom * strength))),
            GlassStyle.Colour => Hue(settings.TintColor, settings.TintStrength),
            _ => (Scaled(top, strength), Scaled(bottom, strength)),
        };
    }

    private static (Color, Color) Hue(string hex, double strength)
    {
        var colour = (Color)ColorConverter.ConvertFromString(hex);
        var alpha = ColourAlpha * strength / DemoTintStrength;
        return (WithAlpha(colour, Math.Min(0.9, alpha)), WithAlpha(colour, Math.Min(0.85, alpha * 0.7)));
    }

    /// <summary>How much of the style's own tint a strength gives: 0.4 of it at 0, all of it at the demo's 0.5, 2.2 times
    /// at 1 (the demo's tuner ran from about a quarter to two and a half times its default).</summary>
    internal static double Strength(double tintStrength)
        => tintStrength <= DemoTintStrength
            ? 0.4 + 0.6 * tintStrength / DemoTintStrength
            : 1 + 1.2 * (tintStrength - DemoTintStrength) / (1 - DemoTintStrength);

    /// <summary>Where the halo is: the GlassPanel template's content takes it as its Effect.</summary>
    public const string HaloKey = "A.Glass.Halo";

    /// <summary>Whether this glass has the halo: not on the strict glass, nor on Aero bloom where text reads at 3:1 without it.</summary>
    public const string HaloOnKey = "A.Glass.HaloOn";

    /// <summary>The halo behind the glass's content (0.10.1): a soft shadow in <paramref name="colour"/>, the ink's
    /// opposite, which GlassMaterial counts as that colour at A.Glass.HaloShare under the text; null (none) on the strict
    /// glass.</summary>
    public static System.Windows.Media.Effects.DropShadowEffect? Halo(GlassSettings settings, Theme theme, Color colour)
    {
        if (Strict(settings.Sanitised())) return null;
        var palette = Palette(theme);
        return Frozen(new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = colour, ShadowDepth = 0, BlurRadius = (double)palette["A.Glass.HaloBlur"], Opacity = (double)palette["A.Glass.HaloOpacity"],
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        });
    }

    /// <summary>Text on the strict glass (Increase contrast, Reduce transparency): WCAG AA.</summary>
    public const double StrictContrast = 4.5;

    /// <summary>Text on the bright glass, over the frost at its brightest with the halo behind it (the owner's choice).</summary>
    public const double GlassContrast = 3.0;

    /// <summary>Whether the glass is the strict one: the frost held within the backdrop bounds, no halo, 4.5:1.</summary>
    public static bool Strict(GlassSettings settings) => settings.IncreaseContrast || settings.ReduceTransparency;

    /// <summary><paramref name="tint"/> drawn toward <paramref name="pole"/> and made denser, in small steps, until
    /// <paramref name="ink"/> reads on it at <paramref name="target"/> over every ground it may sit on.</summary>
    private static Color Toward(Color tint, Color pole, Color ink, Func<Color, Color[]> groundsOf, double target)
    {
        for (var step = 1; step <= 40; step++)
        {
            var t = step / 40.0;
            var mixed = Mix(tint, pole, t);
            var candidate = WithAlpha(mixed, tint.A / 255.0 + (0.92 - tint.A / 255.0) * t);
            if (Worst(ink, groundsOf(candidate)) >= target) return candidate;
        }
        return WithAlpha(pole, 0.92);
    }

    /// <summary><paramref name="well"/> made fainter, a step at a time, until <paramref name="ink"/> reads on it at
    /// <paramref name="target"/> over every ground; the glass under it reads, so it always comes to rest.</summary>
    private static Color Quieter(Color well, Color ink, Color[] grounds, double target)
    {
        for (var alpha = (int)well.A; alpha > 0; alpha--)
        {
            var candidate = Color.FromArgb((byte)alpha, well.R, well.G, well.B);
            if (Worst(ink, grounds.Select(g => Contrast.Over(candidate, g))) >= target) return candidate;
        }
        return Color.FromArgb(0, well.R, well.G, well.B);
    }

    /// <summary><paramref name="fill"/> made denser, a step at a time, until <paramref name="text"/> reads on it at 4.5:1
    /// over both backdrops.</summary>
    private static Color Denser(Color fill, Color text, Color darkest, Color brightest)
    {
        for (var alpha = (int)fill.A; alpha < 255; alpha++)
        {
            var candidate = Color.FromArgb((byte)alpha, fill.R, fill.G, fill.B);
            if (Worst(text, [Contrast.Over(candidate, darkest), Contrast.Over(candidate, brightest)]) >= 4.5) return candidate;
        }
        return WithAlpha(fill, 1);
    }

    /// <summary>The weakest contrast <paramref name="ink"/> makes on any of <paramref name="grounds"/>.</summary>
    private static double Worst(Color ink, IEnumerable<Color> grounds) => grounds.Min(g => Contrast.Ratio(Contrast.Over(ink, g), g));

    /// <summary><paramref name="text"/> with its alpha raised, in steps of 1/255, until it reads at 4.5:1 on every ground.</summary>
    private static Color Readable(Color text, Color[] grounds, double target)
    {
        for (var alpha = (int)text.A; alpha < 255; alpha++)
        {
            var candidate = Color.FromArgb((byte)alpha, text.R, text.G, text.B);
            if (Worst(candidate, grounds) >= target) return candidate;
        }
        return Color.FromArgb(255, text.R, text.G, text.B);
    }

    private static Color Scaled(Color colour, double factor) => WithAlpha(colour, Math.Clamp(colour.A / 255.0 * factor, 0, 1));

    private static Color WithAlpha(Color colour, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), colour.R, colour.G, colour.B);

    private static Color Mix(Color one, Color other, double t)
        => Color.FromRgb((byte)Math.Round(one.R + (other.R - one.R) * t), (byte)Math.Round(one.G + (other.G - one.G) * t), (byte)Math.Round(one.B + (other.B - one.B) * t));

    private static Color Shade(Color colour, double factor)
        => Color.FromArgb(colour.A, (byte)Math.Round(colour.R * factor), (byte)Math.Round(colour.G * factor), (byte)Math.Round(colour.B * factor));

    private static SolidColorBrush Solid(Color colour) => Frozen(new SolidColorBrush(colour));

    private static LinearGradientBrush Vertical(Color top, Color bottom) => Frozen(new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1)));

    /// <summary>The HTML's 140 degree rim, as the palette's A.B.Rim.</summary>
    private static LinearGradientBrush Rim(Dictionary<string, Color> colours) => Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(colours["A.C.RimA"], 0), new(colours["A.C.RimB"], 0.3), new(colours["A.C.RimC"], 0.65), new(colours["A.C.RimD"], 1),
        }, new Point(0.18, 0), new Point(0.82, 1)));

    private static LinearGradientBrush TopSheen(Color sheen) => Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(sheen, 0), new(WithAlpha(sheen, 0), 1) }, new Point(0, 0), new Point(0, 22)) { MappingMode = BrushMappingMode.Absolute });

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Whether a token already holds this value, so an unchanged one is not set again and nothing repaints.</summary>
    private static bool Same(object? old, object? value) => (old, value) switch
    {
        (null, null) => true,
        (System.Windows.Media.Effects.DropShadowEffect a, System.Windows.Media.Effects.DropShadowEffect b)
            => a.Color == b.Color && a.BlurRadius == b.BlurRadius && a.Opacity == b.Opacity && a.ShadowDepth == b.ShadowDepth,
        (Color a, Color b) => a == b,
        (double a, double b) => a == b,
        (SolidColorBrush a, SolidColorBrush b) => a.Color == b.Color && a.Opacity == b.Opacity,
        (LinearGradientBrush a, LinearGradientBrush b) => a.StartPoint == b.StartPoint && a.EndPoint == b.EndPoint && a.MappingMode == b.MappingMode
            && a.GradientStops.Count == b.GradientStops.Count
            && a.GradientStops.Zip(b.GradientStops).All(s => s.First.Color == s.Second.Color && s.First.Offset == s.Second.Offset),
        _ => false,
    };

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Glass) or nameof(SettingsViewModel.Theme) or null)
            _target.Dispatcher.InvokeAsync(Refresh, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Windows' app mode changed: the theme manager swaps the palette first (at normal priority); this follows.</summary>
    private void OnWindowsChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            _target.Dispatcher.InvokeAsync(Refresh, System.Windows.Threading.DispatcherPriority.Background);
    }
}
