using System.IO;
using System.IO.Compression;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class PayloadWindowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-payload-{Guid.NewGuid():N}");

    public PayloadWindowTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_gzipped_file_is_decompressed_and_indented()
    {
        var path = Path.Combine(_folder, "day.json.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new StreamWriter(gzip))
            writer.Write("{\"a\":1,\"b\":[2,3]}");

        var text = PayloadWindow.ReadJson(path);

        text.ShouldContain("\"a\": 1");
        text.ShouldContain("\"b\": [");
    }

    [Fact]
    public void A_plain_json_file_is_read_and_indented_too()
    {
        var path = Path.Combine(_folder, "preview.json");
        File.WriteAllText(path, "{\"a\":1}");

        PayloadWindow.ReadJson(path).ShouldContain("\"a\": 1");
    }

    [Fact]
    public void A_truncated_or_corrupt_gz_file_throws_invalid_data_reading_it_raw()
    {
        // ReadJson itself still throws: it is PayloadWindow's constructor that must turn this into a readable message.
        var path = CorruptGz();

        Should.Throw<InvalidDataException>(() => PayloadWindow.ReadJson(path));
    }

    [Fact]
    public void A_truncated_or_corrupt_gz_file_is_reported_as_a_message_instead_of_crashing_the_window()
    {
        var path = CorruptGz();

        PayloadWindow.SafeReadJson(path).ShouldStartWith("Couldn't read this file:");
    }

    /// <summary>A gzip file whose header is right but whose compressed body is not, so decompressing it fails partway
    /// through rather than at the first byte.</summary>
    private string CorruptGz()
    {
        var path = Path.Combine(_folder, "day.json.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new StreamWriter(gzip))
            writer.Write("{\"a\":1,\"b\":[2,3],\"c\":\"enough text that corrupting the middle lands inside the compressed data\"}");

        var bytes = File.ReadAllBytes(path);
        for (var i = 10; i < Math.Min(bytes.Length, 40); i++) bytes[i] = (byte)~bytes[i];
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
