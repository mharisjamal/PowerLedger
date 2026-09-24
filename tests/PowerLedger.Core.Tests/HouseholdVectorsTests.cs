using System.Text;
using System.Text.Json;
using PowerLedger.Core.Households;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>The vectors the Worker checks with WebCrypto (server/test/fixtures/households/vectors.json) hold for the .NET
/// code too, so a change to either side's formats shows up in both test suites.</summary>
public class HouseholdVectorsTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contract", "households-vectors.json"))).RootElement;

    private static byte[] Bytes(string base64Url) =>
        Convert.FromBase64String(base64Url.Replace('-', '+').Replace('_', '/') + new string('=', (4 - base64Url.Length % 4) % 4));

    [Fact]
    public void The_device_id_is_the_one_its_signing_key_gives() =>
        HouseholdCrypto.DeviceIdOf(Bytes(Vectors.GetProperty("signSpki").GetString()!)).ShouldBe(Vectors.GetProperty("deviceId").GetString());

    [Fact]
    public void The_signed_request_verifies_and_covers_what_the_vector_says()
    {
        var request = Vectors.GetProperty("request");
        var toSign = HouseholdCrypto.RequestToSign(request.GetProperty("method").GetString()!, request.GetProperty("path").GetString()!,
            request.GetProperty("time").GetInt64(), Bytes(request.GetProperty("body").GetString()!));

        Encoding.UTF8.GetString(toSign).ShouldBe(request.GetProperty("signedText").GetString());
        HouseholdCrypto.Verify(Bytes(Vectors.GetProperty("signSpki").GetString()!), toSign, Bytes(request.GetProperty("signature").GetString()!))
            .ShouldBeTrue();
    }
}
