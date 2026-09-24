using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Service.Households.Relay;

namespace PowerLedger.Service.Households;

/// <summary>The JSON households keep in the settings, send between PCs and post to the server, generated at build time.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, ServerEntry>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
[JsonSerializable(typeof(Dictionary<string, Lag>))]
[JsonSerializable(typeof(LanMessage))]
[JsonSerializable(typeof(CreateHouseholdBody))]
[JsonSerializable(typeof(MemberKeysBody))]
[JsonSerializable(typeof(PostKeysBody))]
[JsonSerializable(typeof(KeyEnvelopeReply))]
[JsonSerializable(typeof(MembersReply))]
[JsonSerializable(typeof(BatchPost))]
[JsonSerializable(typeof(BatchPage))]
[JsonSerializable(typeof(BatchPlain))]
[JsonSerializable(typeof(ServerError))]
[JsonSerializable(typeof(List<PendingOp>))]
[JsonSerializable(typeof(SignInBody))]
[JsonSerializable(typeof(SignInReply))]
[JsonSerializable(typeof(LinkBody))]
[JsonSerializable(typeof(RecoveryBody))]
[JsonSerializable(typeof(RecoveryReply))]
[JsonSerializable(typeof(RecoverBody))]
[JsonSerializable(typeof(List<JoinRequestItem>))]
[JsonSerializable(typeof(ApproveBody))]
[JsonSerializable(typeof(Approving))]
[JsonSerializable(typeof(Answering))]
[JsonSerializable(typeof(OwnRequestsReply))]
[JsonSerializable(typeof(CommitBody))]
[JsonSerializable(typeof(NonceBody))]
[JsonSerializable(typeof(AskReply))]
[JsonSerializable(typeof(SealedKeyList))]
[JsonSerializable(typeof(RecoverReply))]
[JsonSerializable(typeof(Recovering))]
[JsonSerializable(typeof(RecoveryPut))]
internal sealed partial class HouseholdJson : JsonSerializerContext
{
    /// <summary>The value, or null when the text is missing or isn't one.</summary>
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

    /// <summary>The value, or null when the bytes are empty or aren't one.</summary>
    public static T? Read<T>(ReadOnlySpan<byte> json, JsonTypeInfo<T> type) where T : class
    {
        if (json.IsEmpty) return null;
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

    public static byte[] Bytes<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(value, type);
}
