using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PowerLedger.App.Aero;

/// <summary>
/// Aero's adaptive ink (Plan AD, the owner 2026-10-15: white text over a white window can't be read), as Apple's vibrancy
/// has it: each glass piece's text and icons follow what that piece shows. Its engine reads the tone of what the piece's
/// glass shows (<see cref="LiquidGlassBackdrop.Tones"/>: 8 by 8 cells of the live screen, the wallpaper or the CPU path's
/// picture, read where the recipe bends each pixel from; for a piece inside another, the outer piece's glass where it
/// lies under its own content); its tint is laid over that; and the ground's relative luminance picks the ink. Bright
/// behind, near black ink with dimmer steps and no text shadow; dark behind, the look's own white. A band between
/// <see cref="ToLight"/> and <see cref="ToDark"/> keeps the ink from flickering, and the change is a short colour ease
/// (none under reduced motion). The glass itself is untouched.
/// <para>The piece takes the ink of its mean. Where its cells disagree (a pane over the edge of a white window, or over
/// the white blotches its glass bends a window into), the regions inside it whose text sits over the other side take
/// that side's ink (<see cref="InkZone"/>), down to a single line of text.</para>
/// <para>A piece (or region) whose ink is the one it inherits lays nothing: on Aero's dark default backdrop nothing
/// changes at all. Otherwise it lays a dictionary of the ink's tokens on its own resources, over every DynamicResource
/// beneath it; its brushes ease from the old ink to the new, and one turning back eases to what it inherits before it
/// lets go.</para>
/// </summary>
internal sealed class GlassInk
{
    /// <summary>The ground's luminance at which white ink turns dark: there white reads at 2.6:1 and the dark ink at 6.9:1.</summary>
    public const double ToDark = 0.36;

    /// <summary>The ground's luminance at which dark ink turns white again: white reads at 3.2:1 there, over the look's
    /// approved 3:1 floor, and the dark ink still at 5.6:1. The mockup's blues and violets lie below.</summary>
    public const double ToLight = 0.28;

    /// <summary>How long the ink eases from one to the other.</summary>
    public const double EaseMs = 250;

    /// <summary>The colour keys of the ink: the text steps, the muted dots, and the accent's chart line.</summary>
    public static readonly string[] Keys = ["A.C.Text", .. GlassMaterial.TextSteps.Select(s => s.Key), "A.C.Muted"];

    internal const string LineKey = "A.C.AccentLine";

    /// <summary>The keys the piece lays, as brushes that ease: each colour key's A.B. brush.</summary>
    internal static readonly string[] Eased = [.. Keys, LineKey];

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

    /// <summary>How soon a piece whose backdrop is part bright, part dark looks again after a layout pass moved its
    /// content.</summary>
    internal static readonly TimeSpan LayoutEvery = TimeSpan.FromMilliseconds(150);

    private readonly GlassPanel _panel;
    private readonly InkZone _zone;
    private readonly Dictionary<FrameworkElement, InkZone> _zones = [];
    private readonly HashSet<FrameworkElement> _walked = [];
    private readonly bool[] _cellDark = new bool[BackdropTone.Cells];
    private readonly double[] _cellLight = new double[BackdropTone.Cells];
    private bool _cellsKnown;
    private bool _seen;
    private bool _mixed;
    private LiquidGlassBackdrop? _backdrop;
    private DispatcherTimer? _again;

    public GlassInk(GlassPanel panel)
    {
        _panel = panel;
        _zone = new InkZone(panel);
        panel.Loaded += (_, _) =>
        {
            Refreshed -= OnRefreshed;
            Refreshed += OnRefreshed;
            Evaluate();
        };
        panel.Unloaded += (_, _) =>
        {
            Refreshed -= OnRefreshed;
            Mixed(false);
        };
    }

    /// <summary>The glass's tokens changed, maybe on another window's thread: looked at on the piece's own.</summary>
    private void OnRefreshed()
    {
        if (_panel.Dispatcher.CheckAccess()) Evaluate();
        else _panel.Dispatcher.BeginInvoke(Evaluate);
    }

    /// <summary>The ink the piece lays of its own now (true dark, false white), or null where it takes what it inherits;
    /// for the tests.</summary>
    internal bool? Laid => _zone.Laid;

    /// <summary>The ink laid on <paramref name="element"/> inside the piece, apart from the piece's, or null; for the
    /// tests.</summary>
    internal bool? LaidOn(FrameworkElement element) => _zones.TryGetValue(element, out var zone) ? zone.Laid : null;

    /// <summary>How many regions inside the piece hold an ink of their own now, for the tests.</summary>
    internal int Zones => _zones.Values.Count(z => !z.Idle);

    /// <summary>The piece's backdrop, from its template.</summary>
    public void Use(LiquidGlassBackdrop? backdrop)
    {
        if (ReferenceEquals(backdrop, _backdrop)) return;
        if (_backdrop != null) _backdrop.ToneChanged -= Evaluate;
        _backdrop = backdrop;
        if (_backdrop != null) _backdrop.ToneChanged += Evaluate;
        Evaluate();
    }

    /// <summary>
    /// Picks the ink for the backdrop's tone now and lays it (or lets go of it): the piece's own from the mean of what it
    /// shows; then, where its cells disagree (a pane over the edge of a white window), the regions inside it whose text
    /// sits over the other side take that side's ink.
    /// </summary>
    public void Evaluate()
    {
        if (InkZone.Inherited(_panel) is not { } inherited) return;
        var instant = !_seen || AeroMotion.Reduced || !_panel.IsLoaded;
        bool changed;
        if (_backdrop?.Tone is not { } tone)
        {
            changed = _zone.Lay(null, inherited, instant);
            _cellsKnown = false;
            changed |= LetGoInside(instant);
            Mixed(false);
        }
        else
        {
            _seen = true;
            var now = _zone.Laid ?? inherited.Dark;
            var dark = Dark(now, Contrast.Luminance(Ground(tone, _panel.Background)));
            changed = _zone.Lay(dark == inherited.Dark ? null : dark, inherited, instant);
            var mixed = Cells(dark);
            Mixed(mixed);
            changed |= mixed ? Zone(dark, instant) : LetGoInside(instant);
        }
        // What the pieces inside inherit changed only if an ink here did; their own backdrops tell them the rest.
        if (changed) Inner();
    }

    /// <summary>Each cell's ink, held through the band as the piece's is (a cell seen for the first time starts from the
    /// piece's ink); true when they disagree.</summary>
    private bool Cells(bool dark)
    {
        var tones = _backdrop!.Tones;
        if (tones.Region.IsEmpty)
        {
            _cellsKnown = false;
            return false;
        }
        var mixed = false;
        for (var c = 0; c < BackdropTone.Cells; c++)
        {
            var colour = BackdropTone.FromLinear(tones.Cells[c * 3], tones.Cells[c * 3 + 1], tones.Cells[c * 3 + 2]);
            _cellLight[c] = Contrast.Luminance(Ground(colour, _panel.Background));
            _cellDark[c] = Dark(_cellsKnown ? _cellDark[c] : dark, _cellLight[c]);
            mixed |= _cellDark[c] != dark;
        }
        _cellsKnown = true;
        return mixed;
    }

    /// <summary>
    /// The regions inside the piece whose text sits over cells of the other ink: walked down from the piece, each element
    /// (its box in the piece's device pixels) over cells of one ink, or with nothing inside it, takes the ink of what lies
    /// under it, and nothing beneath it is looked at; an element over both inks is looked into, and a line of text over both
    /// takes the ink of what lies under it on the whole (held through the band). Shapes, pictures and empty boxes are
    /// passed over. Nested pieces pick their own.
    /// </summary>
    private bool Zone(bool dark, bool instant)
    {
        var dpi = VisualTreeHelper.GetDpi(_panel).DpiScaleX;
        _walked.Clear();
        _changed = false;
        Walk(_panel, Matrix.Identity, dark, dpi, instant);
        var changed = _changed;
        _gone.Clear();
        foreach (var (element, zone) in _zones)
        {
            if (_walked.Contains(element)) continue;
            changed |= LetGo(element, zone, instant);
            if (zone.Idle) _gone.Add(element);
        }
        foreach (var element in _gone) _zones.Remove(element);
        return changed;
    }

    private bool _changed;
    private readonly List<FrameworkElement> _gone = [];

    private void Walk(Visual node, Matrix toPiece, bool dark, double dpi, bool instant)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is not Visual child) continue;
            if (child is LiquidGlassBackdrop or GlassShadow or GlassGlow or GlassPanel or UIElement { IsVisible: false }) continue;
            var local = VisualTreeHelper.GetTransform(child)?.Value ?? Matrix.Identity;
            var offset = VisualTreeHelper.GetOffset(child);
            local.Translate(offset.X, offset.Y);
            var toChild = local * toPiece;
            if (child is not FrameworkElement element)
            {
                Walk(child, toChild, dark, dpi, instant);
                continue;
            }
            var box = InkBox(element);
            box.Transform(toChild);
            if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) continue;
            if (!Under(new Rect(box.X * dpi, box.Y * dpi, box.Width * dpi, box.Height * dpi), out var anyDark, out var anyLight, out var light)) continue;
            var leaf = VisualTreeHelper.GetChildrenCount(element) == 0;
            // A shape, a picture or an empty box draws no words.
            if (leaf && element is Shape or Image or Border or Panel) continue;
            var whole = !(anyDark && anyLight);
            if (whole || leaf)
            {
                var now = _zones.TryGetValue(element, out var zone) && zone.Laid is { } laid ? laid : dark;
                var ink = whole ? anyDark : Dark(now, light);
                if (ink == dark) continue;
                _walked.Add(element);
                if (zone == null) _zones[element] = zone = new InkZone(element);
                if (InkZone.Inherited(element) is { } inherited) _changed |= zone.Lay(ink == inherited.Dark ? null : ink, inherited, instant);
                continue;
            }
            Walk(element, toChild, dark, dpi, instant);
        }
    }

    /// <summary>Where an element's ink lies in its own box: a line of text stretched across its row only where its words
    /// are (its measured width, placed as it aligns them); anything else its whole box.</summary>
    private static Rect InkBox(FrameworkElement element)
    {
        var whole = new Rect(element.RenderSize);
        if (element is not TextBlock text) return whole;
        var width = Math.Min(whole.Width, Math.Max(0, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right));
        var left = text.TextAlignment switch
        {
            TextAlignment.Center => (whole.Width - width) / 2,
            TextAlignment.Right => whole.Width - width,
            _ => 0,
        };
        return new Rect(left, 0, width, whole.Height);
    }

    /// <summary>Which inks the cells under <paramref name="area"/> (the piece's device pixels) take, and the luminance of
    /// what they show, weighted by how much of each the area covers; false when it misses them.</summary>
    private bool Under(Rect area, out bool anyDark, out bool anyLight, out double light)
    {
        (anyDark, anyLight, light) = (false, false, 0);
        var region = _backdrop!.Tones.Region;
        double w = region.Width / BackdropTone.Side, h = region.Height / BackdropTone.Side, total = 0;
        if (w <= 0 || h <= 0) return false;
        int i0 = Math.Max(0, (int)Math.Floor((area.Left - region.Left) / w)), i1 = Math.Min(BackdropTone.Side - 1, (int)Math.Floor((area.Right - region.Left) / w));
        int j0 = Math.Max(0, (int)Math.Floor((area.Top - region.Top) / h)), j1 = Math.Min(BackdropTone.Side - 1, (int)Math.Floor((area.Bottom - region.Top) / h));
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                double left = region.Left + i * w, top = region.Top + j * h;
                var overlap = Math.Max(0, Math.Min(area.Right, left + w) - Math.Max(area.Left, left)) * Math.Max(0, Math.Min(area.Bottom, top + h) - Math.Max(area.Top, top));
                if (overlap <= 0) continue;
                var c = j * BackdropTone.Side + i;
                if (_cellDark[c]) anyDark = true;
                else anyLight = true;
                light += _cellLight[c] * overlap;
                total += overlap;
            }
        }
        if (total <= 0) return false;
        light /= total;
        return true;
    }

    /// <summary>Every region inside the piece back on the piece's ink; true when one was let go.</summary>
    private bool LetGoInside(bool instant)
    {
        if (_zones.Count == 0) return false;
        var changed = false;
        _gone.Clear();
        foreach (var (element, zone) in _zones)
        {
            changed |= LetGo(element, zone, instant);
            if (zone.Idle) _gone.Add(element);
        }
        foreach (var element in _gone) _zones.Remove(element);
        return changed;
    }

    /// <summary>A region back on the piece's ink: eased there, or at once when it has left the piece (an item made again,
    /// a page let go), whose ink then goes with it.</summary>
    private static bool LetGo(FrameworkElement element, InkZone zone, bool instant)
    {
        if (InkZone.Inherited(element) is { } inherited) return zone.Lay(null, inherited, instant || !element.IsLoaded);
        zone.Drop();
        return false;
    }

    /// <summary>While the piece's cells disagree, a layout pass that moves its content has it look again, a little later.</summary>
    private void Mixed(bool on)
    {
        if (on == _mixed) return;
        _mixed = on;
        if (on) _panel.LayoutUpdated += OnLayout;
        else
        {
            _panel.LayoutUpdated -= OnLayout;
            _again?.Stop();
        }
    }

    private void OnLayout(object? sender, EventArgs e)
    {
        _again ??= new DispatcherTimer(LayoutEvery, DispatcherPriority.Background, (_, _) =>
        {
            _again!.Stop();
            if (_mixed) Evaluate();
        }, _panel.Dispatcher);
        if (!_again.IsEnabled) _again.Start();
    }

    /// <summary>The pieces right inside this one look again (and theirs, if theirs change): what they inherit changed.</summary>
    private void Inner()
    {
        var live = GlassPanel.Live;
        for (var i = 0; i < live.Count; i++)
        {
            if (ReferenceEquals(live[i].OuterPanel, _panel)) live[i].Ink.Evaluate();
        }
    }
}

/// <summary>
/// The ink laid on one element (Plan AD): a glass piece, or a region inside one whose text sits over a backdrop of the
/// other side. A dictionary of the ink's tokens on the element's own resources, over every DynamicResource at and beneath
/// it, whose brushes ease from the ink shown to the new one; text that only inherits its colour is pointed at the
/// dictionary's brush of the same step; and the text shadow follows the ink (none under dark ink). Letting go eases back
/// to what the element inherits, then takes it all away.
/// </summary>
internal sealed class InkZone(FrameworkElement host)
{
    private ResourceDictionary? _laid;
    private bool _laidDark;
    private bool _letting;
    private bool _shadowSet;
    private bool _foregroundSet;
    private readonly Dictionary<string, SolidColorBrush> _brushes = [];

    /// <summary>The ink laid now (true dark, false white), or null while none is (or it is being let go).</summary>
    public bool? Laid => _laid == null || _letting ? null : _laidDark;

    /// <summary>Whether nothing is laid at all, nor being let go.</summary>
    public bool Idle => _laid == null;

    /// <summary>Takes everything laid away at once, with nothing to ease to: the element left its piece.</summary>
    public void Drop()
    {
        if (_laid == null) return;
        foreach (var brush in _brushes.Values) brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
        host.Resources.MergedDictionaries.Remove(_laid);
        if (_shadowSet) host.Resources.Remove(GlassMaterial.TextShadowKey);
        if (_foregroundSet) host.ClearValue(TextElement.ForegroundProperty);
        (_laid, _letting, _shadowSet, _foregroundSet) = (null, false, false, false);
    }

    /// <summary>What <paramref name="element"/> inherits: whether its ink is dark, whether the glass is the strict one,
    /// and where to look the rest up (its parent).</summary>
    public static (bool Dark, bool Strict, FrameworkElement From)? Inherited(FrameworkElement element)
    {
        if ((VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)) is not FrameworkElement parent) return null;
        if (parent.TryFindResource("A.C.Text") is not Color text) return null;
        var strict = parent.TryFindResource("A.C.Text2") is Color text2 && text2 == text;
        return (Contrast.Luminance(text) < 0.5, strict, parent);
    }

    /// <summary>Lays <paramref name="dark"/>'s ink (true dark, false white), or lets go (null); true when anything
    /// changed.</summary>
    public bool Lay(bool? dark, (bool Dark, bool Strict, FrameworkElement From) inherited, bool instant)
    {
        if (dark is not { } ink)
        {
            if (_laid == null || _letting) return false;
            // Back to what the element inherits: its colours at once (what a piece inside reads), its brushes eased there,
            // then let go (the same colours, so nothing moves).
            _letting = true;
            var laid = _laid;
            foreach (var key in GlassInk.Eased)
            {
                if (inherited.From.TryFindResource(key) is Color colour) laid[key] = colour;
            }
            Ease(key => inherited.From.TryFindResource(key) is Color c ? c : null, instant, () =>
            {
                if (!ReferenceEquals(laid, _laid) || !_letting) return;
                host.Resources.MergedDictionaries.Remove(laid);
                if (_shadowSet) host.Resources.Remove(GlassMaterial.TextShadowKey);
                if (_foregroundSet) host.ClearValue(TextElement.ForegroundProperty);
                (_laid, _letting, _shadowSet, _foregroundSet) = (null, false, false, false);
            });
            return true;
        }
        if (_laid != null && !_letting && _laidDark == ink) return false;
        var family = GlassInk.Family(ink, inherited.Strict);
        var line = inherited.From.TryFindResource(GlassInk.LineKey) is Color accentLine ? (ink ? GlassInk.OnBright(accentLine) : accentLine) : (Color?)null;
        Color? Target(string key) => key == GlassInk.LineKey ? line : family.TryGetValue(key, out var c) ? c : null;
        var fresh = _laid == null;
        if (fresh)
        {
            _laid = [];
            _brushes.Clear();
            foreach (var key in GlassInk.Eased)
            {
                // Each brush starts at what the element shows now, so the ease runs from there.
                var start = inherited.From.TryFindResource(Brush(key)) is SolidColorBrush shown ? shown.Color : Target(key) ?? Colors.Transparent;
                var brush = new SolidColorBrush(start);
                _brushes[key] = brush;
                _laid[Brush(key)] = brush;
            }
        }
        foreach (var key in GlassInk.Eased)
        {
            if (Target(key) is { } colour) _laid![key] = colour;
        }
        Shadow(ink, inherited.From);
        _laidDark = ink;
        _letting = false;
        // Animated before they are handed out, so nothing freezes them.
        Ease(Target, instant, null);
        if (!fresh) return true;
        host.Resources.MergedDictionaries.Add(_laid!);
        // Text that only inherits its colour (no style or reference of its own): pointed at this dictionary's brush of the
        // step it shows (the same brush it inherits now, or failing that the same colour), so the ink reaches it.
        if (host is GlassPanel || DependencyPropertyHelper.GetValueSource(host, TextElement.ForegroundProperty).BaseValueSource is not (BaseValueSource.Inherited or BaseValueSource.Default)) return true;
        if (host.GetValue(TextElement.ForegroundProperty) is not SolidColorBrush shownInk) return true;
        var step = GlassInk.Keys.FirstOrDefault(k => ReferenceEquals(inherited.From.TryFindResource(Brush(k)), shownInk))
                   ?? GlassInk.Keys.FirstOrDefault(k => inherited.From.TryFindResource(k) is Color c && c == shownInk.Color);
        if (step == null) return true;
        host.SetResourceReference(TextElement.ForegroundProperty, Brush(step));
        _foregroundSet = true;
        return true;
    }


    private static string Brush(string colourKey) => "A.B." + colourKey[4..];

    /// <summary>The text shadow with the ink: none under dark ink (a navy halo would smear it), the look's own under white.
    /// On the element's own resources, and added: a merged dictionary's null reads as no entry at all, and setting null
    /// where there is none sets nothing.</summary>
    private void Shadow(bool dark, FrameworkElement from)
    {
        var key = GlassMaterial.TextShadowKey;
        object? want = dark ? null : Window.GetWindow(host)?.TryFindResource(key);
        if (!dark && ReferenceEquals(from.TryFindResource(key), want))
        {
            if (_shadowSet) host.Resources.Remove(key);
            _shadowSet = false;
            return;
        }
        if (host.Resources.Contains(key)) host.Resources[key] = want;
        else host.Resources.Add(key, want);
        _shadowSet = true;
    }

    /// <summary>
    /// Eases every brush to its target (at once under reduced motion or for the first ink a piece takes). Each brush keeps
    /// an animation on it, at once ones too: WPF freezes a brush a style's DynamicResource hands out (and an icon's pen
    /// freezes its brush) unless it is animated.
    /// </summary>
    private void Ease(Func<string, Color?> target, bool instant, Action? done)
    {
        var ms = instant ? 0 : GlassInk.EaseMs;
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
}
