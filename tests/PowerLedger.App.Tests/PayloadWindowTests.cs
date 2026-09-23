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
}
