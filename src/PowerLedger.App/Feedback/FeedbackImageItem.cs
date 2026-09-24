using System.IO;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>One image attached to a feedback report, already downscaled and re-encoded, with a thumbnail to show it by.</summary>
internal sealed record FeedbackImageItem(string Name, string ContentType, byte[] Data, BitmapSource Thumbnail)
{
    /// <summary>Decodes <paramref name="processed"/> (already through <see cref="ImageProcessing"/>) for display; frozen,
    /// so it can be handed to the UI thread from anywhere.</summary>
    public static BitmapSource DecodeThumbnail(byte[] processed)
    {
        using var stream = new MemoryStream(processed);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
