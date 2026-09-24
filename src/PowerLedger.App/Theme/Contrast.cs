using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>WCAG 2.1's contrast maths, for the tests that hold each palette to its pairs and for anything that picks a
/// colour to read on another.</summary>
internal static class Contrast
{
    /// <summary>The relative luminance of an opaque sRGB colour, 0 for black to 1 for white.</summary>
    public static double Luminance(Color color)
        => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    /// <summary>The contrast ratio between two opaque colours, 1 to 21, whichever is the lighter.</summary>
    public static double Ratio(Color one, Color other)
    {
        var (light, dark) = (Luminance(one), Luminance(other));
        if (light < dark) (light, dark) = (dark, light);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>A translucent colour laid over an opaque one, as the screen shows it.</summary>
    public static Color Over(Color top, Color under)
    {
        var alpha = top.A / 255.0;
        return Color.FromRgb(Mix(top.R, under.R, alpha), Mix(top.G, under.G, alpha), Mix(top.B, under.B, alpha));
    }

    /// <summary>How far apart two opaque colours look: CIE76's ΔE, the distance in CIELAB (D65), 0 for the same colour and
    /// about 2 for the least a person notices. Contrast says whether a mark stands off its ground; this says whether two
    /// marks side by side, like a chart's bands, read as different colours.</summary>
    public static double Difference(Color one, Color other)
    {
        var (l1, a1, b1) = Lab(one);
        var (l2, a2, b2) = Lab(other);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static byte Mix(byte top, byte under, double alpha) => (byte)Math.Round(top * alpha + under * (1 - alpha));

    /// <summary>An sRGB colour in CIELAB, through XYZ with the D65 white.</summary>
    private static (double L, double A, double B) Lab(Color color)
    {
        var (r, g, b) = (Linear(color.R), Linear(color.G), Linear(color.B));
        var x = F((0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047);
        var y = F(0.2126 * r + 0.7152 * g + 0.0722 * b);
        var z = F((0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883);
        return (116 * y - 16, 500 * (x - y), 200 * (y - z));

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
    }

    /// <summary>An sRGB channel undone to linear light.</summary>
    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
