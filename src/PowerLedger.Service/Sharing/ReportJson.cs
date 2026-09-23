using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerLedger.Service.Sharing;

/// <summary>The report's JSON, generated at build time: camelCase, with nulls written except for a section whose switch is off.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReportV1))]
internal sealed partial class ReportJson : JsonSerializerContext
{
    /// <summary>The report as uploaded: compact UTF-8.</summary>
    public static byte[] Write(ReportV1 report) => JsonSerializer.SerializeToUtf8Bytes(report, Default.ReportV1);

    public static ReportV1 Read(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, Default.ReportV1) ?? throw new JsonException("The report was empty.");

    /// <summary>The same JSON indented, and with names and text left readable, for a person to look at.</summary>
    public static byte[] Indented(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            document.WriteTo(writer);
        }
        return buffer.ToArray();
    }

    /// <summary>What the hardware section was, so the next upload can tell whether it changed: the SHA-256 of its JSON.</summary>
    public static string Hash(HardwareDto hardware) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hardware, Default.HardwareDto)));
}
