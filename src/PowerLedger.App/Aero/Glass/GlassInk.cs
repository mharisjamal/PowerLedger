using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerLedger.App.Aero;

/// <summary>
/// Aero's adaptive ink (Plan AD, the owner 2026-10-15: white text over a white window can't be read), as Apple's vibrancy
/// has it: each glass piece's text and icons follow what that piece shows. Its engine reads the tone of the backdrop the
/// piece draws (<see cref="LiquidGlassBackdrop.Tone"/>: the live screen, the wallpaper, the CPU path's picture or, for a
/// piece inside another, the outer piece's content over the outer's own backdrop); its tint is laid over that; and the
/// ground's relative luminance picks the ink. Bright behind, near black ink with dimmer steps and no text shadow; dark
/// behind, the look's own white. A band between <see cref="ToLight"/> and <see cref="ToDark"/> keeps the ink from
/// flickering, and the change is a short colour ease (none under reduced motion). The glass itself is untouched.
/// <para>A piece whose ink is the one it inherits (the window's, or an outer piece's) lays nothing: on Aero's dark default
/// backdrop nothing changes at all. Otherwise it lays a dictionary of the ink's tokens on its own resources, over every
/// DynamicResource beneath it; its brushes ease from the old ink to the new, and a piece turning back eases to what it
/// inherits before it lets go.</para>
/// </summary>
internal sealed class GlassInk
{
    /// <summary>The ground's luminance at which white ink turns dark: there white reads at 2.6:1 and the dark ink at 6:1.</summary>
    public const double ToDark = 0.36;

    /// <summary>The ground's luminance at which dark ink turns white again: white reads at 3.2:1 there, over the look's
    /// approved 3:1 floor, and the dark ink still at 5.6:1. The mockup's blues and violets lie below.</summary>
    public const double ToLight = 0.28;

    /// <summary>How long the ink eases from one to the other.</summary>
    public const double EaseMs = 250;

    /// <summary>The colour keys of the ink: the text steps, the muted dots, and the accent's chart line.</summary>
    public static readonly string[] Keys = ["A.C.Text", .. GlassMaterial.TextSteps.Select(s => s.Key), "A.C.Muted"];

    private const string LineKey = "A.C.AccentLine";

    /// <summary>The keys the piece lays, as brushes that ease: each colour key's A.B. brush.</summary>
    private static readonly string[] Eased = [.. Keys, LineKey];

    /// <summary>The ink for a ground of <paramref name="luminance"/>: true for dark. <paramref name="dark"/> is the ink
    /// now, which holds through the band.</summary>
    public static bool Dark(bool dark, double luminance) => dark ? luminance > ToLight : luminance >= ToDark;

    /// <summary>What the piece's text sits on: its tint (a brush's colour, or a gradient's stops averaged) over the
    /// backdrop's tone.</summary>
    public static Color Ground(Color tone, Brush? tint)
    {
        var over = tint switch
        {
            SolidColorBrush solid => Faded(solid.Color, solid.Opacity),
            GradientBrush { GradientStops.Count: > 0 } gradient => Faded(Average(gradient.GradientStops), gradient.Opacity),
            _ => Colors.Transparent,
        };
        return Contrast.Over(over, Color.FromRgb(tone.R, tone.G, tone.B));
    }

    /// <summary>An opaque grey of relative luminance <paramref name="luminance"/>.</summary>
    public static Color GroundAt(double luminance)
    {
        var v = BackdropTone.FromLinear(luminance, luminance, luminance).R;
        return Color.FromRgb(v, v, v);
    }

    [ThreadStatic]
    private static Dictionary<(bool, bool), IReadOnlyDictionary<string, Color>>? _families;

    /// <summary>
    /// The ink's colours: white, the look's own (GlassMaterial's steps of the dark palette's ink); or dark, the light
    /// palette's near black with the same steps, each made only as dense as it must to read on every ground the dark ink
    /// is used on (from <see cref="ToLight"/> up): text at WCAG AA's 4.5:1, the quietest step and the muted dots at 3:1.
    /// On the strict glass every text step is the ink whole.
    /// </summary>
    public static IReadOnlyDictionary<string, Color> Family(bool dark, bool strict)
    {
        _families ??= [];
        if (_families.TryGetValue((dark, strict), out var made)) return made;
        var palette = GlassMaterial.Palette(dark ? Theme.Light : Theme.Dark);
        var text = (Color)palette["A.C.Text"];
        var family = new Dictionary<string, Color> { ["A.C.Text"] = text, ["A.C.Muted"] = (Color)palette["A.C.Muted"] };
        foreach (var (key, share) in GlassMaterial.TextSteps) family[key] = strict ? text : WithAlpha(text, share);
        if (dark)
        {
            foreach (var key in Keys) family[key] = Dense(family[key], key is "A.C.Text3" or "A.C.Muted" ? 3 : 4.5);
        }
        return _families[(dark, strict)] = family;
    }

    /// <summary>The accent's line on a bright ground: its own hue, darker, until it stands off at 3:1 (WCAG's for a
    /// graphic) on every ground the dark ink is used on.</summary>
    public static Color OnBright(Color accent)
    {
        for (var k = 1.0; k > 0; k -= 0.01)
        {
            var line = Color.FromRgb((byte)Math.Round(accent.R * k), (byte)Math.Round(accent.G * k), (byte)Math.Round(accent.B * k));
            if (Reads(line, 3)) return line;
        }
        return Colors.Black;
    }

    /// <summary><paramref name="ink"/> made denser a step at a time until it reads at <paramref name="least"/>.</summary>
    private static Color Dense(Color ink, double least)
    {
        for (int alpha = ink.A; alpha < 255; alpha++)
        {
            var candidate = Color.FromArgb((byte)alpha, ink.R, ink.G, ink.B);
            if (Reads(candidate, least)) return candidate;
        }
        return Color.FromRgb(ink.R, ink.G, ink.B);
    }

    /// <summary>Whether <paramref name="ink"/> reads at <paramref name="least"/> on every ground from ToLight to white.</summary>
    private static bool Reads(Color ink, double least)
    {
        for (var l = ToLight; l < 1.0001; l += 0.02)
        {
            var ground = GroundAt(Math.Min(l, 1));
            if (Contrast.Ratio(Contrast.Over(ink, ground), ground) < least) return false;
        }
        return true;
    }

    private static Color Average(GradientStopCollection stops)
    {
        double a = 0, r = 0, g = 0, b = 0;
        foreach (var stop in stops)
        {
            var alpha = stop.Color.A / 255.0;
            a += alpha;
            r += stop.Color.R * alpha;
            g += stop.Color.G * alpha;
            b += stop.Color.B * alpha;
        }
        if (a <= 0) return Colors.Transparent;
        return Color.FromArgb((byte)Math.Round(a / stops.Count * 255), (byte)Math.Round(r / a), (byte)Math.Round(g / a), (byte)Math.Round(b / a));
    }

    private static Color Faded(Color colour, double opacity) => WithAlpha(colour, colour.A / 255.0 * opacity);

    private static Color WithAlpha(Color colour, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), colour.R, colour.G, colour.B);

    // ---- One piece's ink ----

    /// <summary>Raised when the glass's settings changed (GlassMaterial), so every piece looks again.</summary>
    private static event Action? Refreshed;

    /// <summary>Every piece looks at its ink again: the glass's tokens changed.</summary>
    public static void RefreshAll() => Refreshed?.Invoke();

    private readonly GlassPanel _panel;
    private LiquidGlassBackdrop? _backdrop;
    private ResourceDictionary? _laid;
    private bool _laidDark;
    private bool _letting;
    private bool _seen;
    private readonly Dictionary<string, SolidColorBrush> _brushes = [];

    public GlassInk(GlassPanel panel)
    {
        _panel = panel;
        panel.Loaded += (_, _) =>
        {
            Refreshed -= Evaluate;
            Refreshed += Evaluate;
            Evaluate();
        };
        panel.Unloaded += (_, _) => Refreshed -= Evaluate;
    }

    /// <summary>Whether the piece lays dark ink of its own now, for the tests.</summary>
    internal bool? Laid => _laid == null || _letting ? null : _laidDark;

    /// <summary>The piece's backdrop, from its template.</summary>
    public void Use(LiquidGlassBackdrop? backdrop)
    {
        if (ReferenceEquals(backdrop, _backdrop)) return;
        if (_backdrop != null) _backdrop.ToneChanged -= Evaluate;
        _backdrop = backdrop;
        if (_backdrop != null) _backdrop.ToneChanged += Evaluate;
        Evaluate();
    }

    /// <summary>Picks the ink for the backdrop's tone now, and lays it (or lets go of it).</summary>
    public void Evaluate()
    {
        if (Inherited() is not { } inherited) return;
        if (_backdrop?.Tone is not { } tone)
        {
            Lay(null, inherited);
            return;
        }
        var now = _laid != null && !_letting ? _laidDark : inherited.Dark;
        var dark = Dark(now, Contrast.Luminance(Ground(tone, _panel.Background)));
        Lay(dark == inherited.Dark ? null : dark, inherited);
    }

    /// <summary>What the piece inherits: whether its ink is dark, and every key's colour and the text shadow.</summary>
    private (bool Dark, bool Strict, Func<string, object?> Find)? Inherited()
    {
        if ((VisualTreeHelper.GetParent(_panel) ?? LogicalTreeHelper.GetParent(_panel)) is not FrameworkElement parent) return null;
        if (parent.TryFindResource("A.C.Text") is not Color text) return null;
        var strict = parent.TryFindResource("A.C.Text2") is Color text2 && text2 == text;
        return (Contrast.Luminance(text) < 0.5, strict, parent.TryFindResource);
    }

    private void Lay(bool? dark, (bool Dark, bool Strict, Func<string, object?> Find) inherited)
    {
        var instant = !_seen || AeroMotion.Reduced || !_panel.IsLoaded;
        _seen |= dark != null || _backdrop?.Tone != null;
        if (dark is not { } ink)
        {
            if (_laid == null || _letting) return;
            // Back to what the piece inherits: eased there, then let go (the same colours, so nothing moves).
            _letting = true;
            var laid = _laid;
            Ease(key => inherited.Find(key) is Color c ? c : null, instant, () =>
            {
                if (!ReferenceEquals(laid, _laid) || !_letting) return;
                _panel.Resources.MergedDictionaries.Remove(laid);
                _panel.Resources.Remove(GlassMaterial.TextShadowKey);
                _laid = null;
                _letting = false;
                Inner();
            });
            return;
        }
        if (_laid != null && !_letting && _laidDark == ink) return;
        var family = Family(ink, inherited.Strict);
        var line = inherited.Find(LineKey) is Color accentLine ? (ink ? OnBright(accentLine) : accentLine) : (Color?)null;
        Color? Target(string key) => key == LineKey ? line : family.TryGetValue(key, out var c) ? c : null;
        var fresh = _laid == null;
        if (fresh)
        {
            _laid = [];
            _brushes.Clear();
            foreach (var key in Eased)
            {
                // Each brush starts at what the piece shows now, so the ease runs from there.
                var start = inherited.Find("A.B." + key[4..]) is SolidColorBrush shown ? shown.Color : Target(key) ?? Colors.Transparent;
                var brush = new SolidColorBrush(start);
                _brushes[key] = brush;
                _laid["A.B." + key[4..]] = brush;
            }
        }
        foreach (var key in Eased)
        {
            if (Target(key) is { } colour) _laid![key] = colour;
        }
        // No text shadow under dark ink (a navy halo would smear it). On the piece's own resources, and added: a merged
        // dictionary's null reads as no entry at all, and setting null where there is none sets nothing.
        if (!ink) _panel.Resources.Remove(GlassMaterial.TextShadowKey);
        else if (!_panel.Resources.Contains(GlassMaterial.TextShadowKey)) _panel.Resources.Add(GlassMaterial.TextShadowKey, null);
        _laidDark = ink;
        _letting = false;
        // Animated before they are handed out, so nothing freezes them.
        Ease(Target, instant, null);
        if (fresh) _panel.Resources.MergedDictionaries.Add(_laid);
        Inner();
    }

    /// <summary>
    /// Eases every brush to its target (at once under reduced motion or for the first ink a piece takes). Each brush keeps
    /// an animation on it, at once ones too: WPF freezes a brush a style's DynamicResource hands out (and an icon's pen
    /// freezes its brush) unless it is animated.
    /// </summary>
    private void Ease(Func<string, Color?> target, bool instant, Action? done)
    {
        var ms = instant ? 0 : EaseMs;
        AnimationClock? last = null;
        foreach (var (key, brush) in _brushes)
        {
            if (target(key) is not { } to) continue;
            var animation = new ColorAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), FillBehavior = FillBehavior.HoldEnd };
            if (ms > 0)
            {
                animation.EasingFunction = AeroMotion.Ease;
            }
            else
            {
                animation.From = to;
                brush.Color = to;
            }
            var clock = animation.CreateClock();
            brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock);
            last = clock;
        }
        if (done == null) return;
        if (last == null || ms <= 0)
        {
            done();
            return;
        }
        last.Completed += (_, _) => done();
    }

    /// <summary>The pieces inside this one look again: what they inherit has changed.</summary>
    private void Inner()
    {
        foreach (var other in GlassPanel.Live.ToArray())
        {
            if (!ReferenceEquals(other, _panel) && _panel.IsAncestorOf(other)) other.Ink.Evaluate();
        }
    }
}
