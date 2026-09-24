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

    private static byte Mix(byte top, byte under, double alpha) => (byte)Math.Round(top * alpha + under * (1 - alpha));

    /// <summary>An sRGB channel undone to linear light.</summary>
    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
