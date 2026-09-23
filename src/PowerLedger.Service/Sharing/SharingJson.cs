using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PowerLedger.Contracts;

namespace PowerLedger.Service.Sharing;

/// <summary>The JSON sharing keeps in the settings and the outbox, and the small bodies it posts, generated at build time.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredConsent))]
[JsonSerializable(typeof(LastSent))]
[JsonSerializable(typeof(SendProblem))]
[JsonSerializable(typeof(Backoff))]
[JsonSerializable(typeof(Dictionary<string, SourceDay>))]
[JsonSerializable(typeof(CrashReport))]
[JsonSerializable(typeof(UsageCounts))]
internal sealed partial class SharingJson : JsonSerializerContext
{
    /// <summary>The value, or null when the text is missing or isn't one: what can't be read counts as never kept.</summary>
    public static T? Read<T>(string? json, JsonTypeInfo<T> type) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Write<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);
}
