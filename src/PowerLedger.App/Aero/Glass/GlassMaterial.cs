using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace PowerLedger.App.Aero;

/// <summary>
/// Turns the Glass settings into the glass (Aero look design §3; 0.10.9, the owner's liquid glass recipe): the A.* tokens
/// a window's pieces, text and controls draw with, put on that window's resources (never Application's) in a dictionary
/// of their own, so every DynamicResource repaints at once and nothing is rebuilt. It listens to
/// <see cref="SettingsViewModel"/>'s one live channel, <c>PropertyChanged</c> for Glass (and Theme), and to Windows'
/// colour settings.
/// <list type="bullet">
/// <item>Style: the recipe has no tint, so Clear and Tinted lay none at the default strength (Tint strength above it
/// lays white up to the mockup's own, 2 % for Clear and 10 % for Tinted); Dark lays the mockup's smoked navy
/// (rgba(12,16,28,.55)), Colour the user's hue at the mockup's 36 %, each scaled by Tint strength around its default.</item>
/// <item>Edge light: the glowing edge's opacity (<see cref="Glow"/>): the owner's 10 % default is the recipe's 70 %, 0
/// is none and 100 % is fully white.</item>
/// <item>Reduce transparency: the engine's live backdrop off (<c>A.Glass.Live</c>) and the style's tint laid dense (86 %)
/// over the tone a desktop averages to, still glass; with Increase contrast, the least dim under the tint that holds
/// every text step at 4.5:1 over a black and a white window, and every step at the ink's full strength. Menus and
/// dialogs are dense enough for their text at 4.5:1 whatever the glass.</item>
/// <item>Accent: the lime or one of four, each with its ink, carried to the shared keys (Midnight's accent, focus, the CPU
/// part and the charts) so the dialogs and the pie follow it, and to the lime glass of an accent action.</item>
/// <item>Reduce motion: AeroMotion's override (null follows Windows).</item>
/// </list>
/// Every piece's content carries the mockup's text shadow (<c>A.Glass.TextShadow</c>).
/// </summary>
internal sealed class GlassMaterial : IDisposable
{
    /// <summary>Where each slider sits by default.</summary>
    public const double DemoTintStrength = 0.5;
    public const double DemoEdgeLight = GlassSettings.DefaultEdgeLight;
    public const double DemoFrost = 0.6;

    /// <summary>The mockup's tints for its four styles (Styles board): Clear white at 2 %, Tinted white at 10 %, Dark
    /// navy at 55 % and Colour the hue at 36 %. Clear and Tinted reach theirs only at full strength.</summary>
    public const double ClearWhite = 0.02;
    public const double TintedWhite = 0.10;
    public static readonly Color DarkTint = Color.FromArgb(0x8C, 12, 16, 28);
    public const double ColourAlpha = 0.36;

    /// <summary>The lime glass of an accent action: the accent at the mockup's 86 %.</summary>
    public const double AccentGlassAlpha = 0.86;

    [ThreadStatic]
    private static Dictionary<Theme, ResourceDictionary>? _palettes;

    /// <summary>The colour keys that follow the ink family rather than the theme: light-on-dark or dark-on-light.</summary>
    private static readonly string[] Family =
    [
        "A.C.Text", "A.C.Text2", "A.C.Text3", "A.C.NavText", "A.C.SegText", "A.C.Well", "A.C.Well2", "A.C.Well3", "A.C.WellHover", "A.C.WellEdge", "A.C.WellTop", "A.C.WellLit", "A.C.SoftEdge", "A.C.SoftTop", "A.C.Line",
        "A.C.ChartWell", "A.C.ChartWellTop", "A.C.ChartWellLit", "A.C.GhostTop", "A.C.Grid", "A.C.Track", "A.C.TrackSoft", "A.C.Muted", "A.C.Ghost", "A.C.PrevLine", "A.C.Guide",
        "A.C.GuideStrong", "A.C.BtnFill", "A.C.BtnFillHover", "A.C.BtnEdge", "A.C.BtnTop", "A.C.OutlineHover", "A.C.NavFillA", "A.C.NavFillB",
        "A.C.NavHover", "A.C.NavIcon", "A.C.NavIndicator", "A.C.NavIndicatorTop", "A.C.SwitchOff", "A.C.MenuHover", "A.C.Ink", "A.C.Pill", "A.C.MenuFill",
        "A.C.MenuEdge", "A.C.ModalTop", "A.C.ModalBottom",
    ];

    /// <summary>The single-colour brushes the palette builds from a token, rebuilt here when their token changes.</summary>
    private static readonly string[] Brushed =
    [
        "Text", "Text2", "Text3", "NavText", "SegText", "GlassHover", "Ink", "Accent", "AccentInk", "Pill", "Well", "Well2", "Well3", "WellHover", "WellEdge", "WellTop", "WellLit", "SoftEdge", "SoftTop", "Line",
        "ChartWell", "ChartWellTop", "ChartWellLit", "GhostTop", "Track", "TrackSoft", "Muted", "BtnFill", "BtnFillHover", "BtnEdge", "BtnTop", "OutlineHover", "AccentTop",
        "AccentHover", "NavHover", "NavIcon", "NavIndicator", "NavIndicatorTop", "SwitchOff", "MenuFill", "MenuEdge", "MenuHover", "Scrim",
        "SeeThroughWash", "AccentGlass",
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

    /// <summary>Raised after the glass was repainted from new settings, for the backdrop.</summary>
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
        var map = Map(Current, _theme());
        foreach (var (key, value) in map)
        {
            if (!_applied.Contains(key) || !Same(_applied[key], value)) _applied[key] = value;
        }
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

    /// <summary>The key of the text shadow every piece's content carries (the mockup's <c>.t</c>).</summary>
    public const string TextShadowKey = "A.Glass.TextShadow";

    /// <summary>The key of the glowing edge's opacity.</summary>
    public const string GlowKey = "A.Glass.Glow";

    /// <summary>The key of whether the engine's live backdrop draws.</summary>
    public const string LiveKey = "A.Glass.Live";

    /// <summary>
    /// The tokens <paramref name="settings"/> give in <paramref name="theme"/>: every A.* colour, brush and number the
    /// glass draws with, and the shared keys the accent reaches. At the default settings each equals the palette's own but
    /// the text steps, the menus and dialogs, and the text shadow.
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
        var strict = Strict(settings);

        // The tint, and on the strict glass its dim.
        var (top, bottom) = Tint(settings);
        if (settings.ReduceTransparency)
        {
            // Denser: the tint laid over the tone a dark or light scene averages to, at ReducedAlpha, so what is behind
            // still shows through a little.
            var frosted = Mix(darkest, brightest, 0.5);
            top = WithAlpha(Contrast.Over(top, frosted), ReducedAlpha);
            bottom = WithAlpha(Contrast.Over(bottom, frosted), ReducedAlpha);
        }
        // Which ink: the theme's own, but light on the smoked Dark style, and on a Colour hue whichever reads on it better.
        var light = Palette(Theme.Dark);
        var dark = Palette(Theme.Light);
        var family = settings.Style switch
        {
            GlassStyle.Dark => light,
            GlassStyle.Colour => Better(Opaque(Hue(settings.TintColor)), light, dark, palette),
            _ => palette,
        };
        if (!ReferenceEquals(family, palette))
        {
            foreach (var key in Family) colours[key] = (Color)family[key];
        }
        if (strict)
        {
            var reads = (Color)family["A.C.Text"];
            var pole = ReferenceEquals(family, light) ? Colors.Black : Colors.White;
            var inWell = (Color)family["A.C.Well"];
            top = Held(top, pole, reads, inWell, StrictContrast);
            bottom = Held(bottom, pole, reads, inWell, StrictContrast);
        }
        colours["A.C.GlassTintTop"] = top;
        colours["A.C.GlassTintBottom"] = bottom;

        // Text: every step at the ink's full strength on the strict glass; otherwise the mockup's steps (.sub at 80 %).
        var text = colours["A.C.Text"];
        if (strict)
        {
            colours["A.C.Text2"] = colours["A.C.Text3"] = colours["A.C.NavText"] = colours["A.C.SegText"] = text;
        }
        else
        {
            colours["A.C.Text2"] = WithAlpha(text, VideoText2);
            colours["A.C.Text3"] = WithAlpha(text, VideoText3);
            colours["A.C.NavText"] = WithAlpha(text, NavText);
            colours["A.C.SegText"] = WithAlpha(text, SegText);
        }
        if (settings.IncreaseContrast) colours["A.C.SeeThroughWash"] = Scaled(colours["A.C.SeeThroughWash"], 2);

        // Menus and dialogs: as dense as their text needs over either backdrop; denser still under Reduce transparency,
        // and glass even then.
        foreach (var key in new[] { "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom" })
        {
            var fill = Denser(colours[key], colours["A.C.Text2"], darkest, brightest);
            colours[key] = settings.ReduceTransparency ? WithAlpha(fill, Math.Max(fill.A / 255.0, ReducedMenuAlpha)) : fill;
        }

        // The accent, and its glass.
        var accentName = settings.Accent.ToString();
        var accent = colours["A.C.Accent." + accentName];
        var ink = colours["A.C.AccentInk." + accentName];
        colours["A.C.Accent"] = accent;
        colours["A.C.AccentInk"] = ink;
        colours["A.C.AccentGlass"] = WithAlpha(accent, AccentGlassAlpha);

        var result = new Dictionary<string, object>();
        foreach (var (key, colour) in colours) result[key] = colour;
        foreach (var name in Brushed) result["A.B." + name] = Solid(colours["A.C." + name]);
        result["A.B.GlassTint"] = Vertical(top, bottom);
        result["A.B.NavFill"] = Frozen(new LinearGradientBrush(colours["A.C.NavFillA"], colours["A.C.NavFillB"], new Point(0, 0), new Point(1, 0)));
        result["A.B.ModalFill"] = Vertical(colours["A.C.ModalTop"], colours["A.C.ModalBottom"]);
        result["A.Glass.Frost"] = (double)palette["A.Glass.Frost"] * settings.Frost / DemoFrost;
        result[GlowKey] = Glow(settings.EdgeLight);
        result[LiveKey] = !settings.ReduceTransparency;
        result[TextShadowKey] = TextShadow(colours["A.C.TextShadow"]);

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

    /// <summary>
    /// The glowing edge's opacity for an Edge light: the owner's default (10 %) is the recipe's 70 %, below it the glow
    /// fades to none at 0, above it it rises to fully white at 100 %.
    /// </summary>
    public static double Glow(double edgeLight)
    {
        var edge = GlassSettings.Fraction(edgeLight, GlassSettings.DefaultEdgeLight);
        var recipe = LiquidGlassRecipe.HighlightOpacity;
        return edge <= GlassSettings.DefaultEdgeLight
            ? recipe * edge / GlassSettings.DefaultEdgeLight
            : recipe + (1 - recipe) * (edge - GlassSettings.DefaultEdgeLight) / (1 - GlassSettings.DefaultEdgeLight);
    }

    /// <summary>The mockup's text shadow, <c>0 1px 2px</c> in <paramref name="colour"/>: a Gaussian of deviation 1 (half the
    /// blur) one unit down, which WPF's blur radius of three deviations draws.</summary>
    public static DropShadowEffect TextShadow(Color colour) => Frozen(new DropShadowEffect
    {
        Color = Color.FromRgb(colour.R, colour.G, colour.B), Opacity = colour.A / 255.0, ShadowDepth = 1, Direction = 270, BlurRadius = TextShadowBlur,
        RenderingBias = RenderingBias.Performance,
    });

    /// <summary>WPF's BlurRadius for the text shadow's deviation of 1.</summary>
    public const double TextShadowBlur = 3;

    /// <summary>A palette by theme, loaded once on this thread.</summary>
    internal static ResourceDictionary Palette(Theme theme)
    {
        _palettes ??= [];
        if (!_palettes.TryGetValue(theme, out var palette)) _palettes[theme] = palette = ThemeManager.Palette(Look.Aero, theme);
        return palette;
    }

    /// <summary>The tint's top and foot for the style (the mockup's, the same top to foot), scaled by strength.</summary>
    internal static (Color Top, Color Bottom) Tint(GlassSettings settings)
    {
        var strength = settings.TintStrength;
        var above = Math.Max(0, (strength - DemoTintStrength) / (1 - DemoTintStrength));
        Color tint = settings.Style switch
        {
            GlassStyle.Clear => WithAlpha(Colors.White, ClearWhite * above),
            GlassStyle.Tinted => WithAlpha(Colors.White, TintedWhite * above),
            GlassStyle.Dark => WithAlpha(DarkTint, Math.Min(MaxDim, DarkTint.A / 255.0 * Strength(strength))),
            _ => WithAlpha(Hue(settings.TintColor), Math.Min(MaxDim, ColourAlpha * Strength(strength))),
        };
        return (tint, tint);
    }

    private static Color Hue(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Opaque(Color colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    /// <summary>Of <paramref name="one"/>'s and <paramref name="other"/>'s ink, the family whose ink reads better on
    /// <paramref name="ground"/>; <paramref name="own"/> when they read alike.</summary>
    private static ResourceDictionary Better(Color ground, ResourceDictionary one, ResourceDictionary other, ResourceDictionary own)
    {
        double On(ResourceDictionary inks) => Contrast.Ratio((Color)inks["A.C.Text"], ground);
        var (a, b) = (On(one), On(other));
        if (Math.Abs(a - b) < 0.5) return own;
        return a > b ? one : other;
    }

    /// <summary>How much of the style's own tint a strength gives: 0.4 of it at 0, all of it at the default 0.5, 2.2 times
    /// at 1.</summary>
    internal static double Strength(double tintStrength)
        => tintStrength <= DemoTintStrength
            ? 0.4 + 0.6 * tintStrength / DemoTintStrength
            : 1 + 1.2 * (tintStrength - DemoTintStrength) / (1 - DemoTintStrength);

    /// <summary>The mockup's quieter text steps, as shares of the ink: its .sub at 80 %, and a quieter one at 60 %.</summary>
    public const double VideoText2 = 0.8;
    public const double VideoText3 = 0.6;

    /// <summary>The mockup's sidebar pages (88 %) and a segmented control's words (85 %).</summary>
    public const double NavText = 0.88;
    public const double SegText = 0.85;

    /// <summary>How dense Reduce transparency makes the tint, and at least the menus and dialogs: still glass.</summary>
    public const double ReducedAlpha = 0.86;
    public const double ReducedMenuAlpha = 0.94;

    /// <summary>Text on the strict glass (Increase contrast, Reduce transparency): WCAG AA.</summary>
    public const double StrictContrast = 4.5;

    /// <summary>Whether the glass is the strict one: a tint as dense as the text needs at 4.5:1, every text step whole.</summary>
    public static bool Strict(GlassSettings settings) => settings.IncreaseContrast || settings.ReduceTransparency;

    /// <summary>The densest dim laid under a tint.</summary>
    public const double MaxDim = 0.92;

    /// <summary>
    /// The least of <paramref name="pole"/> (black under light ink, white under dark) to lay under <paramref name="tint"/>
    /// so <paramref name="ink"/> reads at <paramref name="target"/> on the glass, and on <paramref name="well"/> on the
    /// glass, whether a black window or a white one is behind it; null when no dim up to <see cref="MaxDim"/> does.
    /// </summary>
    internal static double? Dim(Color tint, Color pole, Color ink, Color well, double target)
    {
        for (var step = 0; step <= (int)Math.Round(MaxDim * 200); step++)
        {
            var dim = step / 200.0;
            if (Reads(ink, Layered(tint, WithAlpha(pole, dim)), well) >= target) return dim;
        }
        return null;
    }

    /// <summary><paramref name="tint"/> over the least dim that makes <paramref name="ink"/> read on it; where none does (a
    /// tint already nearly solid in a colour the ink can't read on), the tint itself drawn toward
    /// <paramref name="pole"/> and made denser, in small steps, until it reads.</summary>
    internal static Color Held(Color tint, Color pole, Color ink, Color well, double target)
    {
        if (Dim(tint, pole, ink, well, target) is { } dim) return Layered(tint, WithAlpha(pole, dim));
        for (var step = 1; step <= 40; step++)
        {
            var t = step / 40.0;
            var candidate = WithAlpha(Mix(tint, pole, t), tint.A / 255.0 + (MaxDim - tint.A / 255.0) * t);
            if (Reads(ink, candidate, well) >= target) return candidate;
        }
        return WithAlpha(pole, MaxDim);
    }

    /// <summary>The weakest contrast <paramref name="ink"/> makes on <paramref name="glass"/>, and on
    /// <paramref name="well"/> on it, over a black and over a white ground.</summary>
    internal static double Reads(Color ink, Color glass, Color well)
        => new[] { Colors.Black, Colors.White }.Min(behind =>
        {
            var ground = Contrast.Over(glass, behind);
            var inWell = Contrast.Over(well, ground);
            return Math.Min(Contrast.Ratio(Contrast.Over(ink, ground), ground), Contrast.Ratio(Contrast.Over(ink, inWell), inWell));
        });

    /// <summary>Two translucent layers as the one they make: <paramref name="over"/> laid on <paramref name="under"/>.</summary>
    internal static Color Layered(Color over, Color under)
    {
        double a = over.A / 255.0, b = under.A / 255.0 * (1 - a), alpha = a + b;
        if (alpha <= 0) return Color.FromArgb(0, over.R, over.G, over.B);
        byte Channel(byte o, byte u) => (byte)Math.Round((o * a + u * b) / alpha);
        return Color.FromArgb((byte)Math.Round(alpha * 255), Channel(over.R, under.R), Channel(over.G, under.G), Channel(over.B, under.B));
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

    private static Color Scaled(Color colour, double factor) => WithAlpha(colour, Math.Clamp(colour.A / 255.0 * factor, 0, 1));

    private static Color WithAlpha(Color colour, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), colour.R, colour.G, colour.B);

    private static Color Mix(Color one, Color other, double t)
        => Color.FromRgb((byte)Math.Round(one.R + (other.R - one.R) * t), (byte)Math.Round(one.G + (other.G - one.G) * t), (byte)Math.Round(one.B + (other.B - one.B) * t));

    private static Color Shade(Color colour, double factor)
        => Color.FromArgb(colour.A, (byte)Math.Round(colour.R * factor), (byte)Math.Round(colour.G * factor), (byte)Math.Round(colour.B * factor));

    private static SolidColorBrush Solid(Color colour) => Frozen(new SolidColorBrush(colour));

    private static LinearGradientBrush Vertical(Color top, Color bottom) => Frozen(new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1)));

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
        (DropShadowEffect a, DropShadowEffect b)
            => a.Color == b.Color && a.BlurRadius == b.BlurRadius && a.Opacity == b.Opacity && a.ShadowDepth == b.ShadowDepth && a.Direction == b.Direction,
        (Color a, Color b) => a == b,
        (double a, double b) => a == b,
        (bool a, bool b) => a == b,
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
