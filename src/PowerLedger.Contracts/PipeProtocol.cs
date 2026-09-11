using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PowerLedger.Contracts;

/// <summary>The wire format the service and the App share (spec §8).</summary>
public static class PipeProtocol
{
    /// <summary>Opened as \\.\pipe\PowerLedger.v1. A breaking protocol change gets a new name.</summary>
    public const string PipeName = "PowerLedger.v1";

    /// <summary>The largest message either side accepts, newline excluded.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>One message as a line of UTF-8 JSON ending in a newline.</summary>
    public static byte[] Serialize(PipeMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, PipeJson.Default.PipeMessage);
        if (json.Length > MaxMessageBytes)
            throw new PipeProtocolException($"A {message.GetType().Name} came to {json.Length} bytes, over the 64 KB limit.");
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>One line, newline removed, back into a message. Anything that is not one is a <see cref="PipeProtocolException"/>.</summary>
    public static PipeMessage Deserialize(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, PipeJson.Default.PipeMessage)
                ?? throw new PipeProtocolException("The message was empty.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new PipeProtocolException("The line was not a PowerLedger message.", error);
        }
    }

    /// <summary>
    /// Stored settings are read and written by reflection, not by the generated code. The generated code assigns every
    /// init-only property, so a setting missing from JSON an older version wrote would come back as zero instead of its
    /// default, and so would a profile field. Reflection builds the object first and sets only what the JSON holds.
    /// </summary>
    private static readonly JsonSerializerOptions StoredSettings = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Settings as the service stores them.</summary>
    public static string SerializeSettings(ServiceSettings settings) => JsonSerializer.Serialize(settings, StoredSettings);

    /// <summary>Stored settings back, or null when the text is not settings at all.</summary>
    public static ServiceSettings? DeserializeSettings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceSettings>(json, StoredSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The other end broke the protocol: a message too long, not JSON, or of an unknown kind.</summary>
public sealed class PipeProtocolException(string message, Exception? inner = null) : Exception(message, inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipeMessage))]
internal sealed partial class PipeJson : JsonSerializerContext;
