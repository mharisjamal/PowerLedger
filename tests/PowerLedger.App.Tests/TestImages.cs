using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Tests;

/// <summary>Small PNG-encoded test images, built on the WPF imaging pipeline's own STA thread.</summary>
internal static class TestImages
{
    /// <summary>A width x height PNG. Noisy fills it with pseudo-random pixels so it compresses poorly, to force Send
    /// feedback's own re-encoding to fall back to JPEG in a test; a plain one stays small and so stays PNG.</summary>
    public static byte[] Png(int width, int height, bool noisy = false) => Sta.Run(() =>
    {
        var pixels = new byte[width * height * 4];
        if (noisy) new Random(1).NextBytes(pixels);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    });
}
