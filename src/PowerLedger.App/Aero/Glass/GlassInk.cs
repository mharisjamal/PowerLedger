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
/// has it: every word, icon and line on a glass piece follows what the glass shows right behind it. The engine reads the
/// tone of what each piece's glass shows (<see cref="LiquidGlassBackdrop.Tones"/>: cells about a line of text high of the
/// live screen, the wallpaper or the CPU path's picture, each read where the recipe bends its pixels from; for a piece
/// inside another, the outer piece's glass where it lies under its own content); the piece's tint, and the fills of the
/// wells and pills an element sits in, are laid over that; and the grounds' relative luminance picks the ink. Bright
/// behind, near black ink with dimmer steps, dark tracks and lines, and no text shadow; dark behind, the look's own white,
/// held (each step denser) where the ground is too bright for the look's quieter steps. The glass itself is untouched.
/// <para>The piece takes the ink of its mean. Where its cells disagree (a pane over the edge of a white window, or over the
/// white blotches its glass bends a window into), its content is walked: an element over cells that all want one ink takes
/// it whole, and a word, an icon or a line over both takes the ink whose least contrast over every cell its bounds cover
/// is the greater (<see cref="Dark(bool, double, double, double, double)"/>). A band between <see cref="ToLight"/> and
/// <see cref="ToDark"/> (and a margin between the two inks' least contrasts) keeps the ink from flickering, and the change
/// is a short colour ease (none under reduced motion).</para>
/// <para>A piece (or region) whose ink is the one it inherits lays nothing: on Aero's dark default backdrop nothing
/// changes at all. Otherwise it lays a dictionary of the ink's tokens on its own resources, over every DynamicResource
/// beneath it (every Aero foreground, hover, pressed and chosen states too, goes through these tokens); its brushes ease from
/// the old ink to the new, and one turning back eases to what it inherits before it lets go (<see cref="InkZone"/>).</para>
/// </summary>
internal sealed class GlassInk
{
    /// <summary>The ground's luminance at which white ink turns dark: up to it full white reads at the look's approved 3:1
    /// (3.07:1 on the ground itself), and the dark ink at 5.7:1 there.</summary>
    public const double ToDark = 0.29;

    /// <summary>The ground's luminance at which dark ink turns white again: white reads at 3.7:1 there, and the dark ink
    /// still at 4.8:1. The mockup's deeper blues and violets lie below.</summary>
    public const double ToLight = 0.235;

    /// <summary>Over grounds that differ, how much greater one ink's least contrast must be than the other's to take its
    /// place: a tenth.</summary>
    public const double Margin = 1.1;

    /// <summary>The least contrast an ink must hold over the worst ground under a word for that ground to decide: where
    /// neither ink holds 2:1 over its worst (a word half over a black blotch, half over white glass), the worst is lost
    /// either way and the word goes with most of what lies under it.</summary>
    public const double Hold = 2;

    /// <summary>The relative luminance of the dark ink (the light palette's #16181C).</summary>
    public static readonly double DarkInkLuminance = Contrast.Luminance(Color.FromRgb(0x16, 0x18, 0x1C));

    /// <summary>How long the ink eases from one to the other.</summary>
    public const double EaseMs = 250;

    /// <summary>The colour keys of the ink's words: the text steps and the muted dots.</summary>
    public static readonly string[] Keys = ["A.C.Text", .. GlassMaterial.TextSteps.Select(s => s.Key), "A.C.Muted"];

    /// <summary>The colour keys of the ink's lines: bars' tracks, rules, a chart's grid and guides, and the hairlines between
    /// a glass group's buttons (whose brush, A.B.GroupSep, has no colour key of its own).</summary>
    public static readonly string[] Graphics = ["A.C.Track", "A.C.TrackSoft", "A.C.Line", "A.C.Grid", "A.C.PrevLine", "A.C.Guide", "A.C.GuideStrong", "A.C.GroupSep"];

    internal const string LineKey = "A.C.AccentLine";

    /// <summary>The keys the piece lays, as brushes that ease: each colour key's A.B. brush.</summary>
    internal static readonly string[] Eased = [.. Keys, .. Graphics, LineKey];

    /// <summary>The ink for a ground of <paramref name="luminance"/>: true for dark. <paramref name="dark"/> is the ink
    /// now, which holds through the band.</summary>
    public static bool Dark(bool dark, double luminance) => Dark(dark, luminance, luminance, 1, 1);

    /// <summary>
    /// The ink for words over grounds from luminance <paramref name="least"/> to <paramref name="most"/> (every cell their
    /// bounds cover): the one whose least contrast over them is the greater, the white ink's over the brightest ground, the
    /// dark ink's over the darkest. <paramref name="dark"/> is the ink now, which holds unless the other is greater by
    /// <see cref="Margin"/>; white holds over grounds no brighter than <see cref="ToDark"/>, and comes back over grounds no
    /// brighter than <see cref="ToLight"/>, as over one ground. Where neither holds <see cref="Hold"/> over its worst (the
    /// worst is lost either way), the ink that reads at 3:1 over more of the word: <paramref name="whiteReads"/> and
    /// <paramref name="darkReads"/>, the shares of its bounds each reads over, the ink now holding unless the other's is
    /// greater by a tenth.
    /// </summary>
    public static bool Dark(bool dark, double least, double most, double whiteReads, double darkReads)
    {
        if (least > most) (least, most) = (most, least);
        var white = 1.05 / (most + 0.05);
        var black = (least + 0.05) / (DarkInkLuminance + 0.05);
        if (Math.Max(white, black) < Hold) return dark ? whiteReads <= darkReads + 0.1 : darkReads > whiteReads + 0.1;
        return dark
            ? !(most <= ToLight || white > Margin * black)
            : most >= ToDark && black > Margin * white;
    }

    /// <summary>The brightest ground the look's own white steps all read on at 3:1 (the quietest, Text3 at 60 %, stops
    /// first): past it, the ground still too dark for the dark ink, white is laid held (<see cref="Family"/>'s, each step
    /// as dense as it must be up to <see cref="ToDark"/>); back under <see cref="HeldBack"/>.</summary>
    public static readonly double HeldFrom = OwnStepsHold(Colors.White);

    public static readonly double HeldBack = HeldFrom - 0.03;

    /// <summary>The mirror for the light look's own dark ink: the darkest ground its own steps all read on at 3:1; under
    /// it, the ground still too bright for white, the dark ink is laid held; back over <see cref="DarkHeldBack"/>.</summary>
    public static readonly double DarkHeldFrom = OwnStepsHold(Color.FromRgb(0x16, 0x18, 0x1C));

    public static readonly double DarkHeldBack = DarkHeldFrom + 0.03;

    /// <summary>Whether the ink (<paramref name="dark"/>) is held over a ground of <paramref name="light"/> (the brightest
    /// under white, the darkest under the dark ink); <paramref name="held"/> is whether it is now, which holds through the
    /// margin.</summary>
    public static bool Held(bool dark, bool held, double light) => dark
        ? light <= DarkHeldFrom || (held && light < DarkHeldBack)
        : light >= HeldFrom || (held && light > HeldBack);

    /// <summary>How far from its own side (black for white ink, white for dark) the ground can go with the quietest of the
    /// look's own steps of <paramref name="ink"/> still reading at 3:1.</summary>
    private static double OwnStepsHold(Color ink)
    {
        var step = Color.FromArgb((byte)Math.Round(GlassMaterial.TextSteps.Min(s => s.Share) * 255), ink.R, ink.G, ink.B);
        var by = Contrast.Luminance(ink) > 0.5 ? 0.001 : -0.001;
        var l = by > 0 ? 0.0 : 1.0;
        for (; l + by is >= 0 and <= 1; l += by)
        {
            var ground = GroundAt(l + by);
            if (Contrast.Ratio(Contrast.Over(step, ground), ground) < 3) break;
        }
        return l;
    }

    /// <summary>The brightest ground white ink reads on at 3:1, and the darkest the dark ink does.</summary>
    private const double WhiteReadsUpTo = 1.05 / 3 - 0.05;
    private static readonly double DarkReadsFrom = 3 * (DarkInkLuminance + 0.05) - 0.05;

    /// <summary>What the piece's text sits on: its tint (a brush's colour, or a gradient's stops averaged) over the
    /// backdrop's tone.</summary>
    public static Color Ground(Color tone, Brush? tint) => Contrast.Over(Tint(tint), Color.FromRgb(tone.R, tone.G, tone.B));

    /// <summary>A fill as one translucent colour: a brush's colour, or a gradient's stops averaged, at its opacity; clear for
    /// anything else.</summary>
    public static Color Tint(Brush? fill, double opacity = 1) => fill switch
    {
        SolidColorBrush solid => Faded(solid.Color, solid.Opacity * opacity),
        GradientBrush { GradientStops.Count: > 0 } gradient => Faded(Average(gradient.GradientStops), gradient.Opacity * opacity),
        _ => Colors.Transparent,
    };

    /// <summary>An opaque grey of relative luminance <paramref name="luminance"/>.</summary>
    public static Color GroundAt(double luminance)
    {
        var v = BackdropTone.FromLinear(luminance, luminance, luminance).R;
        return Color.FromRgb(v, v, v);
    }

    [ThreadStatic]
    private static Dictionary<(bool, bool), IReadOnlyDictionary<string, Color>>? _families;

    /// <summary>
    /// The ink's colours, each step of the ink made only as dense as it must to read on every ground the ink is laid on:
    /// text at WCAG AA's 4.5:1 where it can, the quietest step and the muted dots at 3:1. Dark: the light palette's near
    /// black, from <see cref="ToLight"/> up, and its dark tracks and lines. White, held: the dark palette's white up to
    /// <see cref="ToDark"/> (where full white reads 3:1, so each step is all but whole; where the look's own white steps
    /// read, nothing is laid and they show), and the look's own tracks and lines. On the strict glass every text step is
    /// the ink whole.
    /// </summary>
    public static IReadOnlyDictionary<string, Color> Family(bool dark, bool strict)
    {
        _families ??= [];
        if (_families.TryGetValue((dark, strict), out var made)) return made;
        var palette = GlassMaterial.Palette(dark ? Theme.Light : Theme.Dark);
        var text = (Color)palette["A.C.Text"];
        var family = new Dictionary<string, Color> { ["A.C.Text"] = text, ["A.C.Muted"] = (Color)palette["A.C.Muted"] };
        foreach (var (key, share) in GlassMaterial.TextSteps) family[key] = strict ? text : WithAlpha(text, share);
        foreach (var key in Keys) family[key] = Dense(family[key], key is "A.C.Text3" or "A.C.Muted" ? 3 : 4.5, dark);
        foreach (var key in Graphics)
        {
            family[key] = palette.Contains(key) ? (Color)palette[key] : ((SolidColorBrush)palette["A.B." + key[4..]]).Color;
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
            if (Reads(line, 3, dark: true)) return line;
        }
        return Colors.Black;
    }

    /// <summary><paramref name="ink"/> made denser a step at a time until it reads at <paramref name="least"/> on every
    /// ground its ink (<paramref name="dark"/>) is used on, or whole where nothing less does.</summary>
    private static Color Dense(Color ink, double least, bool dark)
    {
        for (int alpha = ink.A; alpha < 255; alpha++)
        {
            var candidate = Color.FromArgb((byte)alpha, ink.R, ink.G, ink.B);
            if (Reads(candidate, least, dark)) return candidate;
        }
        return Color.FromRgb(ink.R, ink.G, ink.B);
    }

    /// <summary>Whether <paramref name="ink"/> reads at <paramref name="least"/> on every ground its ink is used on: the
    /// dark ink's from ToLight to white, the white ink's from black to ToDark.</summary>
    private static bool Reads(Color ink, double least, bool dark)
    {
        var (from, to) = dark ? (ToLight, 1.0) : (0.0, ToDark);
        for (var i = 0; i <= 40; i++)
        {
            var ground = GroundAt(from + (to - from) * i / 40);
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
    private readonly List<FrameworkElement> _gone = [];
    private double[] _cellLight = [];
    private Color _tint;
    private bool _seen;
    private bool _mixed;
    private bool _changed;
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

    /// <summary>How many of those hold the dark ink, for the tests.</summary>
    internal int DarkZones => _zones.Values.Count(z => z.Laid == true);

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
    /// shows, white held where every cell passes <see cref="HeldFrom"/>; then, where its cells disagree, the regions inside
    /// it whose words sit over the other side (or over cells that want the white held) take that.
    /// </summary>
    public void Evaluate()
    {
        if (InkZone.Inherited(_panel) is not { } inherited) return;
        var instant = !_seen || AeroMotion.Reduced || !_panel.IsLoaded;
        bool changed;
        if (_backdrop?.Tone is not { } tone)
        {
            (_held, _heldInside) = (false, false);
            changed = _zone.Lay(null, inherited, instant);
            changed |= LetGoInside(instant);
            Mixed(false);
        }
        else
        {
            _seen = true;
            var now = _zone.Laid ?? inherited.Dark;
            var light = Contrast.Luminance(Ground(tone, _panel.Background));
            var dark = Dark(now, light);
            var (least, most) = (light, light);
            var mixed = Cells(dark, ref least, ref most);
            // The ink over grounds its look's own quieter steps no longer read on (white over the brighter, the light look's
            // dark over the darker): laid held where every cell is (unless it comes held already; the other ink than the
            // one inherited is laid held anyway); where only some are, the words over those take it.
            var other = dark != inherited.Dark;
            _held = other || Held(dark, _held, dark ? most : least);
            _heldInside = !_held && Held(dark, _heldInside, dark ? least : most);
            mixed |= _heldInside;
            if (mixed && _backdrop!.Tones.Count > 0) Sums(_backdrop.Tones.Cols, _backdrop.Tones.Rows);
            changed = _zone.Lay(other || (_held && !inherited.Held) ? dark : null, inherited, instant);
            Mixed(mixed);
            changed |= mixed ? Zone(InkOf(dark, _held), instant) : LetGoInside(instant);
        }
        // What the pieces inside inherit changed only if an ink here did; their own backdrops tell them the rest.
        if (changed) Inner();
    }

    private bool _held;
    private bool _heldInside;

    /// <summary>What an element shows: white or the dark ink, each the look's own or held (its steps denser,
    /// <see cref="Family"/>'s).</summary>
    private enum Ink
    {
        White,
        WhiteHeld,
        Dark,
        DarkHeld,
    }

    private static Ink InkOf(bool dark, bool held) => dark ? held ? Ink.DarkHeld : Ink.Dark : held ? Ink.WhiteHeld : Ink.White;

    private static bool IsDark(Ink ink) => ink is Ink.Dark or Ink.DarkHeld;

    private static bool IsHeld(Ink ink) => ink is Ink.WhiteHeld or Ink.DarkHeld;

    /// <summary>Each cell's ground (the piece's tint over what its glass shows there) as a luminance, the darkest and the
    /// brightest into <paramref name="least"/> and <paramref name="most"/> (left as they are where there are no cells);
    /// true when a cell wants the other ink than the piece's.</summary>
    private bool Cells(bool dark, ref double least, ref double most)
    {
        var tones = _backdrop!.Tones;
        var count = tones.Count;
        if (count == 0) return false;
        if (_cellLight.Length < count) _cellLight = new double[count];
        _tint = Tint(_panel.Background);
        var cells = tones.Cells;
        (least, most) = (1, 0);
        for (var c = 0; c < count; c++)
        {
            var light = _tint.A == 0 ? BackdropTone.Luminance(cells[c]) : BackdropTone.LuminanceOver(_tint, cells[c]);
            _cellLight[c] = light;
            least = Math.Min(least, light);
            most = Math.Max(most, light);
        }
        return Dark(dark, least) != dark || Dark(dark, most) != dark;
    }

    /// <summary>How many cells, from the top left to each corner, want each answer (<see cref="Want"/>): an element's cells
    /// all agree, or not, in four reads however many it covers.</summary>
    private void Sums(int cols, int rows)
    {
        var size = (cols + 1) * (rows + 1);
        if (_sums[0].Length < size)
        {
            for (var k = 0; k < _sums.Length; k++) _sums[k] = new int[size];
        }
        Span<int> row = stackalloc int[_sums.Length];
        foreach (var sums in _sums) Array.Clear(sums, 0, cols + 1);
        for (var j = 0; j < rows; j++)
        {
            row.Clear();
            int at = (j + 1) * (cols + 1), above = j * (cols + 1);
            foreach (var sums in _sums) sums[at] = 0;
            for (var i = 0; i < cols; i++)
            {
                var light = _cellLight[j * cols + i];
                for (var k = 0; k < _sums.Length; k++)
                {
                    if (Wants((Want)k, light)) row[k]++;
                    _sums[k][at + i + 1] = _sums[k][above + i + 1] + row[k];
                }
            }
        }
    }

    /// <summary>What a cell is counted for: wanting the dark ink coming from white, and staying dark; wanting white held
    /// coming to it, and staying; wanting the dark ink held coming to it, and staying.</summary>
    private enum Want
    {
        DarkFromWhite,
        DarkStays,
        WhiteHeldFrom,
        WhiteHeldStays,
        DarkHeldFrom,
        DarkHeldStays,
    }

    private static bool Wants(Want want, double light) => want switch
    {
        Want.DarkFromWhite => Dark(false, light),
        Want.DarkStays => Dark(true, light),
        Want.WhiteHeldFrom => Held(false, false, light),
        Want.WhiteHeldStays => Held(false, true, light),
        Want.DarkHeldFrom => Held(true, false, light),
        _ => Held(true, true, light),
    };

    private readonly int[][] _sums = [[], [], [], [], [], []];

    /// <summary>The cells in columns <paramref name="i0"/> to <paramref name="i1"/> and rows <paramref name="j0"/> to
    /// <paramref name="j1"/> that want <paramref name="want"/>.</summary>
    private int Count(Want want, int cols, int i0, int i1, int j0, int j1)
    {
        var sums = _sums[(int)want];
        var w = cols + 1;
        return sums[(j1 + 1) * w + i1 + 1] - sums[j0 * w + i1 + 1] - sums[(j1 + 1) * w + i0] + sums[j0 * w + i0];
    }

    /// <summary>
    /// The regions inside the piece whose words sit over cells of the other ink (or, on the look's own ink, over cells
    /// that want it held): walked down from the piece, each element (its ink's bounds in the piece's device pixels) over
    /// cells that all want one ink takes it, and nothing beneath it is looked at; an element over both is looked into, and
    /// a word, an icon or a line over both takes the ink whose least contrast over them is the greater, held where the
    /// ground under it on the far side (the brightest under white, the darkest under the dark ink) wants it. The fills an
    /// element sits in (a well, a pill's tint, a button's) are laid over the cells first. Shapes, pictures and empty boxes
    /// are passed over; nested pieces pick their own.
    /// </summary>
    private bool Zone(Ink piece, bool instant)
    {
        var dpi = VisualTreeHelper.GetDpi(_panel).DpiScaleX;
        _walked.Clear();
        _changed = false;
        Walk(_panel, Matrix.Identity, Colors.Transparent, piece, dpi, instant);
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

    private void Walk(Visual node, Matrix toPiece, Color fill, Ink piece, double dpi, bool instant)
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
                Walk(child, toChild, fill, piece, dpi, instant);
                continue;
            }
            var leaf = VisualTreeHelper.GetChildrenCount(element) == 0;
            var background = element switch
            {
                // A control's own Background is its template's to draw or not (a theme's white under a bare radio button):
                // only what a panel, a box or a line of text fills is counted.
                Panel panel => panel.Background,
                Border border => border.Background,
                TextBlock text => text.Background,
                _ => null,
            };
            var own = Tint(background, element.Opacity);
            // A picture, an empty panel or a bare box draws nothing the ink colours; a box that fills (a bar's track, a
            // rule) is a line of the ink, a shape too.
            if (leaf && (element is Image or Panel || element is Border && own.A == 0)) continue;
            // The well, pill or button it sits in, over what lies under it: what its words sit on (a leaf's own fill is
            // its ink, not its ground).
            var under = own.A == 0 || leaf ? fill : GlassMaterial.Layered(own, fill);
            var box = InkBox(element);
            box.Transform(toChild);
            if (box.IsEmpty || box.Width <= 0 || box.Height <= 0) continue;
            var area = new Rect(box.X * dpi, box.Y * dpi, box.Width * dpi, box.Height * dpi);
            // What takes the ink: the element, or for a control's content presenter, the content itself (a control's
            // content inherits and looks its resources up through the control, past the presenter and the template's parts
            // above it).
            var host = element is ContentPresenter { Content: FrameworkElement content } && ReferenceEquals(VisualTreeHelper.GetParent(content), element) ? content : element;
            _zones.TryGetValue(host, out var zone);
            var now = zone?.Laid is { } laid ? InkOf(laid, true) : piece;
            bool whole, dark, held;
            if (!leaf && under.A == 0)
            {
                // A container over bare glass: whether its cells all agree, from the sums, however large it is.
                if (!_backdrop!.Tones.Covered(area, out var i0, out var i1, out var j0, out var j1)) continue;
                var cols = _backdrop.Tones.Cols;
                var n = (i1 - i0 + 1) * (j1 - j0 + 1);
                var wantDark = Count(IsDark(now) ? Want.DarkStays : Want.DarkFromWhite, cols, i0, i1, j0, j1);
                dark = wantDark == n;
                var heldNow = IsHeld(now) && IsDark(now) == dark;
                var wantHeld = Count(dark ? heldNow ? Want.DarkHeldStays : Want.DarkHeldFrom : heldNow ? Want.WhiteHeldStays : Want.WhiteHeldFrom, cols, i0, i1, j0, j1);
                held = wantHeld > 0;
                whole = (wantDark == 0 || wantDark == n) && (wantHeld == 0 || wantHeld == n);
            }
            else
            {
                if (!Extremes(area, under, out var least, out var most, out var whiteReads, out var darkReads)) continue;
                var nowDark = IsDark(now);
                var lower = Dark(nowDark, least);
                var agree = lower == Dark(nowDark, most);
                dark = agree ? lower : Dark(nowDark, least, most, whiteReads, darkReads);
                // Held where the ground under it on the far side wants it: the brightest under white, the darkest under
                // the dark ink.
                var heldNow = IsHeld(now) && nowDark == dark;
                held = Held(dark, heldNow, dark ? least : most);
                whole = agree && held == Held(dark, heldNow, dark ? most : least);
            }
            // The other ink than the piece's is laid held, and the piece's own held stays so.
            if (dark != IsDark(piece) || IsHeld(piece)) held = true;
            var ink = InkOf(dark, held);
            // A part of a control's template above its content (its grid, its border) is looked into, never laid on: what
            // it lays would not reach the content. Nor is a piece held as content: it picks its own.
            if (!leaf && ((element.TemplatedParent is Control && element is not ContentPresenter) || host is GlassPanel)) whole = false;
            if (whole || leaf)
            {
                if (ink == piece) continue;
                _walked.Add(host);
                // Laid already: nothing to look up again.
                if (zone?.Laid is { } on && InkOf(on, true) == ink) continue;
                if (zone == null) _zones[host] = zone = new InkZone(host);
                if (InkZone.Inherited(host) is { } inherited)
                    _changed |= zone.Lay(dark != inherited.Dark || (held && !inherited.Held) ? dark : null, inherited, instant);
                continue;
            }
            // Looked into now: an ink laid on it before lets go first (its colours at once), so what lies inside it
            // inherits the piece's again.
            if (zone is { Idle: false } && ReferenceEquals(host, element)) _changed |= LetGo(element, zone, instant);
            Walk(element, toChild, under, piece, dpi, instant);
        }
    }

    /// <summary>Where an element's ink lies in its own box: a line of text stretched across its row only where its words
    /// are (its measured width, placed as it aligns them); anything else its whole box.</summary>
    internal static Rect InkBox(FrameworkElement element)
    {
        var whole = new Rect(element.RenderSize);
        if (element is not TextBlock text) return whole;
        // A set width (a percent right aligned in a column of its own) is no measure of the words: they are measured.
        var words = double.IsNaN(text.Width) && text.MinWidth <= 0
            ? text.DesiredSize.Width - text.Margin.Left - text.Margin.Right
            : new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
        var width = Math.Min(whole.Width, Math.Max(0, words));
        var left = text.TextAlignment switch
        {
            TextAlignment.Center => (whole.Width - width) / 2,
            TextAlignment.Right => whole.Width - width,
            _ => 0,
        };
        return new Rect(left, 0, width, whole.Height);
    }

    /// <summary>The darkest and the brightest ground among every cell <paramref name="area"/> (the piece's device pixels)
    /// covers, <paramref name="fill"/> laid over each, and the shares of the area white ink and the dark ink read over at
    /// 3:1, each cell weighted by how much of it the area covers and taken as a mix of the darkest and the brightest
    /// ground in the proportion its mean says (a cell over a window's edge is part window, part what lies beside it);
    /// false when it covers none.</summary>
    private bool Extremes(Rect area, Color fill, out double least, out double most, out double whiteReads, out double darkReads)
    {
        (least, most, whiteReads, darkReads) = (1, 0, 0, 0);
        var tones = _backdrop!.Tones;
        if (!tones.Covered(area, out var i0, out var i1, out var j0, out var j1)) return false;
        var cells = tones.Cells;
        // The fill over the piece's tint, as one colour over each cell.
        var top = fill.A == 0 ? fill : _tint.A > 0 ? GlassMaterial.Layered(fill, _tint) : fill;
        double Light(int c) => fill.A == 0 ? _cellLight[c] : BackdropTone.LuminanceOver(top, cells[c]);
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var light = Light(j * tones.Cols + i);
                least = Math.Min(least, light);
                most = Math.Max(most, light);
            }
        }
        var (whiteDark, whiteBright) = (least <= WhiteReadsUpTo ? 1 : 0, most <= WhiteReadsUpTo ? 1 : 0);
        var (darkDark, darkBright) = (least >= DarkReadsFrom ? 1 : 0, most >= DarkReadsFrom ? 1 : 0);
        double total = 0;
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var bright = most - least > 1e-6 ? Math.Clamp((Light(j * tones.Cols + i) - least) / (most - least), 0, 1) : 0;
                var cell = tones.CellAt(i, j);
                var overlap = Math.Max(0, Math.Min(area.Right, cell.Right) - Math.Max(area.Left, cell.Left)) * Math.Max(0, Math.Min(area.Bottom, cell.Bottom) - Math.Max(area.Top, cell.Top));
                whiteReads += overlap * (whiteDark * (1 - bright) + whiteBright * bright);
                darkReads += overlap * (darkDark * (1 - bright) + darkBright * bright);
                total += overlap;
            }
        }
        if (total > 0) (whiteReads, darkReads) = (whiteReads / total, darkReads / total);
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
/// The ink laid on one element (Plan AD): a glass piece, or a region inside one whose words sit over a backdrop of the
/// other side. A dictionary of the ink's tokens on the element's own resources, over every DynamicResource at and beneath
/// it, whose brushes ease from the ink shown to the new one; words that only inherit their colour are pointed at the
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

    /// <summary>What <paramref name="element"/> inherits: whether its ink is dark, whether the glass is the strict one
    /// (GlassMaterial's mark), whether its ink is held already (its quietest step as dense as the held ink's, as the strict
    /// glass's whole one is), and where to look the rest up (its parent).</summary>
    public static (bool Dark, bool Strict, bool Held, FrameworkElement From)? Inherited(FrameworkElement element)
    {
        if ((VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)) is not FrameworkElement parent) return null;
        if (parent.TryFindResource("A.C.Text") is not Color text) return null;
        var dark = Contrast.Luminance(text) < 0.5;
        var strict = parent.TryFindResource(GlassMaterial.StrictKey) is bool marked ? marked : parent.TryFindResource("A.C.Text2") is Color text2 && text2 == text;
        var held = parent.TryFindResource("A.C.Text3") is Color text3 && text3.A >= GlassInk.Family(dark, strict)["A.C.Text3"].A;
        return (dark, strict, held, parent);
    }

    /// <summary>A key's colour as <paramref name="from"/> finds it: its colour key, or its brush's colour (a key with a brush
    /// alone, as A.B.GroupSep).</summary>
    private static Color? Found(FrameworkElement from, string key)
        => from.TryFindResource(key) is Color colour ? colour : from.TryFindResource(Brush(key)) is SolidColorBrush brush ? brush.Color : null;

    /// <summary>Lays <paramref name="dark"/>'s ink (true dark, false white), or lets go (null); true when anything
    /// changed.</summary>
    public bool Lay(bool? dark, (bool Dark, bool Strict, bool Held, FrameworkElement From) inherited, bool instant)
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
            Ease(key => Found(inherited.From, key), instant, () =>
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
        // Words that only inherit their colour (no style or reference of their own): pointed at this dictionary's brush of
        // the step they show (the same brush they inherit now, or failing that the same colour), so the ink reaches them.
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
