using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;

namespace PowerLedger.App;

/// <summary>Shows one payload file, read-only and monospaced, decompressing a <c>.gz</c> and indenting the JSON
/// (data-sharing design §2). Used for "See what would be sent" and for each row of <see cref="SentWindow"/>.</summary>
public partial class PayloadWindow : Window
{
    internal PayloadWindow(string path)
    {
        InitializeComponent();
        Title = Path.GetFileName(path);
        try
        {
            Body.Text = ReadJson(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Body.Text = "Couldn't read this file: " + error.Message;
        }
    }

    /// <summary>The JSON a payload file holds, decompressed when it is gzipped, indented whatever it was on disk.</summary>
    internal static string ReadJson(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes is [0x1f, 0x8b, ..])
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            gzip.CopyTo(expanded);
            bytes = expanded.ToArray();
        }
        using var document = JsonDocument.Parse(bytes);
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }
}
