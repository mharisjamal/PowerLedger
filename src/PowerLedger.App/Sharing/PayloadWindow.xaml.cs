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
        Body.Text = SafeReadJson(path);
    }

    /// <summary>The JSON a payload file holds, or a readable message when it can't be read: truncated or otherwise
    /// corrupt, not really gzip despite its name, not really JSON once decompressed, or simply gone or locked.</summary>
    internal static string SafeReadJson(string path)
    {
        try
        {
            return ReadJson(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return "Couldn't read this file: " + error.Message;
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
