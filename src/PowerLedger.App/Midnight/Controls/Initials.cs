using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// A household member as a disc of one or two letters (plan O M1-2), the disc's colour picked from a ring of six by the
/// name, so the same PC keeps its colour from one opening to the next and two PCs seldom share one. The letters are white
/// or a fixed near-black, whichever contrasts more with the disc, so they read on a pastel and on a deep colour alike, in
/// either theme.
/// </summary>
internal sealed class Initials : Instrument
{
    public static readonly DependencyProperty MemberProperty = DependencyProperty.Register(
        nameof(Member), typeof(string), typeof(Initials), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Initials), new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GoodBrushProperty = Register<Brush>(nameof(GoodBrush), Brushes.Gray, typeof(Initials));
    public static readonly DependencyProperty WarnBrushProperty = Register<Brush>(nameof(WarnBrush), Brushes.Gray, typeof(Initials));

    public const int RingSize = 6;

    /// <summary>The dark letters' colour: fixed, not the palette's ground, which is pale in the light theme (review 10).</summary>
    internal static readonly Color NearBlack = Color.FromRgb(0x0A, 0x0C, 0x10);

    /// <summary>The name the disc stands for.</summary>
    public string Member { get => (string)GetValue(MemberProperty); set => SetValue(MemberProperty, value); }

    /// <summary>The disc's diameter.</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public Brush GoodBrush { get => (Brush)GetValue(GoodBrushProperty); set => SetValue(GoodBrushProperty, value); }

    public Brush WarnBrush { get => (Brush)GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }

    /// <summary>The disc's letters: the first of the first and last words, or the first two of a single word, or "?" for no name.</summary>
    internal static string Letters(string? name)
    {
        var words = (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length switch
        {
            0 => "?",
            1 => words[0].Length > 1 ? words[0][..2] : words[0],
            _ => $"{words[0][0]}{words[^1][0]}",
        };
        return letters.ToUpperInvariant();
    }

    /// <summary>The name's place in the ring, 0 to 5, the same on every PC and run: FNV-1a over the characters.</summary>
    internal static int Slot(string? name)
    {
        var hash = 2166136261u;
        foreach (var c in name ?? string.Empty) hash = (hash ^ c) * 16777619u;
        return (int)(hash % RingSize);
    }

    /// <summary>The letters' colour on <paramref name="disc"/>: white or <see cref="NearBlack"/>, whichever has the higher
    /// contrast ratio with it (WCAG, as Contrast reckons it), so 4.5:1 wherever either reaches it (review 10).</summary>
    internal static Color LettersOn(Color disc)
        => Contrast.Ratio(Colors.White, disc) >= Contrast.Ratio(NearBlack, disc) ? Colors.White : NearBlack;

    internal override string Describe() => string.IsNullOrWhiteSpace(Member) ? "Unnamed PC" : Member;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Size;
        if (!(size > 0)) return;
        var disc = Disc();
        var centre = new Point(size / 2, size / 2);
        dc.DrawEllipse(disc, null, centre, size / 2, size / 2);
        var ink = new SolidColorBrush(disc is SolidColorBrush solid ? LettersOn(solid.Color) : Colors.White);
        ink.Freeze();
        var letters = Text(Letters(Member), Math.Round(size * 0.4), ink, FontWeights.SemiBold);
        dc.DrawText(letters, new Point(centre.X - letters.Width / 2, centre.Y - letters.Height / 2));
    }

    private Brush Disc() => Slot(Member) switch
    {
        0 => CpuBrush,
        1 => GpuBrush,
        2 => DisplayBrush,
        3 => AccentBrush,
        4 => GoodBrush,
        _ => WarnBrush,
    };
}
