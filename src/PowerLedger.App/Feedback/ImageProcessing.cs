using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>Downscales and re-encodes an image for Send feedback: at most 1600 px on the long side, PNG if that comes to
/// 1 MB or less, else JPEG at quality 80 and down until it does.</summary>
internal static class ImageProcessing
{
    public const int MaxDimension = 1600;
    public const int MaxBytes = 1024 * 1024;
    private const int StartingJpegQuality = 80;
    private const int MinJpegQuality = 20;
    private const int JpegQualityStep = 15;

    /// <summary>Decodes, downscales and re-encodes; null if the bytes aren't a readable image.</summary>
    public static (byte[] Data, string ContentType)? Process(byte[] source)
    {
        BitmapFrame decoded;
        try
        {
            using var input = new MemoryStream(source);
            decoded = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        var scaled = Downscale(decoded);
        var png = Encode(scaled, new PngBitmapEncoder());
        if (png.Length <= MaxBytes) return (png, "image/png");

        byte[] jpeg = png;
        for (var quality = StartingJpegQuality; quality > MinJpegQuality; quality -= JpegQualityStep)
        {
            jpeg = Encode(scaled, new JpegBitmapEncoder { QualityLevel = quality });
            if (jpeg.Length <= MaxBytes) return (jpeg, "image/jpeg");
        }
        jpeg = Encode(scaled, new JpegBitmapEncoder { QualityLevel = MinJpegQuality });
        return (jpeg, "image/jpeg");   // as small as this goes; still sent rather than dropped
    }

    private static BitmapSource Downscale(BitmapSource source)
    {
        var longSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longSide <= MaxDimension) return source;
        var scale = (double)MaxDimension / longSide;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] Encode(BitmapSource source, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
