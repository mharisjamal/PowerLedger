using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// A glass piece's own CSS box-shadow, outside it (0.10.9's audit): how far it drops (<see cref="Y"/>), its blur (the
/// Gaussian's deviation is half of it), its spread (negative shrinks it) and its colour. The mockup has three: a bubble on
/// glass (.bubble), a glass pill (the kit's .pill, which casts no drop-shadow filter of its own) and Energy each day's bars.
/// </summary>
public readonly record struct GlassLift(double Y, double Blur, double Spread, Color Colour)
{
    /// <summary>.bubble: <c>0 8px 18px -8px rgba(0,8,40,.5)</c>.</summary>
    public static readonly GlassLift Bubble = new(8, 18, -8, Color.FromArgb(0x80, 0, 8, 40));

    /// <summary>The kit's .pill: <c>0 10px 22px -12px rgba(0,8,40,.55)</c>.</summary>
    public static readonly GlassLift Pill = new(10, 22, -12, Color.FromArgb(0x8C, 0, 8, 40));

    /// <summary>Energy each day's bar: <c>0 8px 16px -10px rgba(0,8,40,.55)</c>.</summary>
    public static readonly GlassLift Bar = new(8, 16, -10, Color.FromArgb(0x8C, 0, 8, 40));

    /// <summary>How far round the piece the shadow is drawn: three deviations and the spread.</summary>
    public double Reach => (3 * Blur / 2) + Math.Max(0, Spread) + 1;
}
