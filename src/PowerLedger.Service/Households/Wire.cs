using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>A PC in the household as the wire carries it: in a welcome, an approval and a recovery, in a sync's <c>have</c> and a
/// batch's members, and each batch's own device.</summary>
/// <param name="Name">Its name; absent from a removed member's entry.</param>
/// <param name="Kind">"laptop" or "desktop"; absent from a removed member's entry.</param>
/// <param name="Sign">Its signing key, SubjectPublicKeyInfo in base64url; absent from a batch's own device.</param>
/// <param name="Dh">Its key-agreement key, likewise.</param>
/// <param name="Added">When it was added, unix milliseconds, as the list's PC knows it: for display only.</param>
/// <param name="Removed">For a removed member: when it was removed, unix milliseconds: for display only.</param>
/// <param name="AddedEpoch">The household epoch the PC that added it was at (plan 0.9).</param>
/// <param name="RemovedEpoch">For a removed member: the epoch the PC that removed it was at.</param>
internal sealed record WireMember(
    string Id, string? Name, string? Kind, string? Sign = null, string? Dh = null, long? Added = null, long? Removed = null, int? AddedEpoch = null,
    int? RemovedEpoch = null);

/// <summary>An hour row as the wire carries it (plan 0.6); the device it is from goes with the message or batch holding it.</summary>
internal sealed record WireRow(
    long Hour, double EnergyWh, double CpuWh, double GpuWh, double DisplayWh, double RestWh, double IdleOnWh, double IdleOffWh,
    double OnS, double BatteryS, double IdleS, double MeasuredS, double CalibratedS, double EstimatedS, long? CostMicro, string? Currency,
    long Changed);

/// <summary>A PC in the household as the pairing and sync code handles it, its keys as bytes.</summary>
internal sealed record MemberInfo(string Id, string Name, ChassisKind Kind, byte[] Sign, byte[] Dh);

/// <summary>What households send between PCs, and how it is checked on the way in: base64url keys, P-256 only, names cut to size.</summary>
internal static partial class Wire
{
    public const int MaxName = 40;
    public const int MaxMembers = 16;
    private static readonly string P256 = ECCurve.NamedCurves.nistP256.Oid.Value!;

    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>The bytes, or null when the text isn't base64url.</summary>
    public static byte[]? Decode(string? text)
    {
        if (text is null) return null;
        try
        {
            return Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A P-256 public key's SubjectPublicKeyInfo decoded from base64url; null when it is anything else.</summary>
    public static byte[]? PublicKey(string? text) => Decode(text) is { } spki && IsP256(spki) ? spki : null;

    public static bool IsP256(byte[] spki)
    {
        try
        {
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            var curve = key.ExportParameters(false).Curve;
            return read == spki.Length && key.KeySize == 256 && curve.IsNamed
                && (curve.Oid.Value == P256 || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "ECDH_P256");
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static string Kind(ChassisKind kind) => kind == ChassisKind.Laptop ? "laptop" : "desktop";

    public static ChassisKind? Kind(string? kind) => kind switch
    {
        "laptop" => ChassisKind.Laptop,
        "desktop" => ChassisKind.Desktop,
        _ => null,
    };

    /// <summary>A name as the App may show it: trimmed, without control characters, nor the format characters that can hide
    /// text or turn it round, such as U+202E (right-to-left override), nor line and paragraph separators; at most
    /// <see cref="MaxName"/> characters; null when nothing is left.</summary>
    public static string? Name(string? name)
    {
        if (name is null) return null;
        var clean = new string([.. name.Where(Shown)]).Trim();
        if (clean.Length > MaxName) clean = clean[..MaxName].TrimEnd();
        return clean.Length > 0 ? clean : null;
    }

    private static bool Shown(char character) =>
        !char.IsControl(character) && char.GetUnicodeCategory(character) is not
            (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator);

    public static bool IsDeviceId(string? id) => id is not null && Hex32().IsMatch(id);

    /// <summary>A household ID as this service makes them: 16 random bytes as lower-case hex.</summary>
    public static bool IsHouseholdId(string? id) => IsDeviceId(id);

    public static string NewHouseholdId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>What a joining PC signs so a member can add it on the server: <c>powerledger join|{hid}|{sign}|{dh}</c>, its
    /// keys as the base64url it is added under. Only the PC holding the signing key can make it, so no member, and not the
    /// server, can add a PC that didn't ask to join.</summary>
    public static byte[] JoinProof(string householdId, string sign, string dh) =>
        System.Text.Encoding.UTF8.GetBytes($"powerledger join|{householdId}|{sign}|{dh}");

    /// <summary>This PC's proof for joining <paramref name="householdId"/>.</summary>
    public static byte[] SignJoin(DeviceKeys keys, string householdId) =>
        HouseholdCrypto.SignData(keys.Sign, JoinProof(householdId, Encode(keys.SignPublic), Encode(keys.DhPublic)));

    /// <summary>True when <paramref name="proof"/> is the joining PC's own signature over its join.</summary>
    public static bool IsJoinProof(MemberInfo joiner, string householdId, byte[]? proof) =>
        proof is not null && HouseholdCrypto.Verify(joiner.Sign, JoinProof(householdId, Encode(joiner.Sign), Encode(joiner.Dh)), proof);

    public static WireMember Member(MemberInfo member) =>
        new(member.Id, member.Name, Kind(member.Kind), Encode(member.Sign), Encode(member.Dh));

    public static WireMember Member(HouseholdMember member) =>
        new(member.DeviceId, member.Name, Kind(member.Kind), Encode(member.SignKey), Encode(member.DhKey));

    /// <summary>A member as sent, checked: its ID must be the one its signing key makes. Null when anything is wrong.</summary>
    public static MemberInfo? Member(WireMember? member)
    {
        if (member is null || Name(member.Name) is not { } name || Kind(member.Kind) is not { } kind) return null;
        if (PublicKey(member.Sign) is not { } sign || PublicKey(member.Dh) is not { } dh) return null;
        return HouseholdCrypto.DeviceIdOf(sign) == member.Id ? new MemberInfo(member.Id, name, kind, sign, dh) : null;
    }

    public static WireRow Row(HouseholdRow row) => new(
        row.HourMs, row.EnergyWh, row.CpuWh, row.GpuWh, row.DisplayWh, row.RestWh, row.IdleOnWh, row.IdleOffWh,
        row.OnS, row.BatteryS, row.IdleS, row.MeasuredS, row.CalibratedS, row.EstimatedS, row.CostMicro, row.Currency, row.ChangedMs);

    /// <summary>A row as sent, for <paramref name="deviceId"/>, checked: whole hours, finite figures, a currency of three
    /// capitals or none. Null when anything is wrong.</summary>
    public static HouseholdRow? Row(string deviceId, WireRow? row)
    {
        if (row is null || row.Hour % 3_600_000 != 0 || row.Changed <= 0) return null;
        double[] figures =
        [
            row.EnergyWh, row.CpuWh, row.GpuWh, row.DisplayWh, row.RestWh, row.IdleOnWh, row.IdleOffWh, row.OnS, row.BatteryS, row.IdleS,
            row.MeasuredS, row.CalibratedS, row.EstimatedS,
        ];
        if (!figures.All(double.IsFinite)) return null;
        if (row.Currency is not null && !Currency().IsMatch(row.Currency)) return null;
        if ((row.Currency is null) != (row.CostMicro is null)) return null;
        return new HouseholdRow(
            deviceId, row.Hour, row.EnergyWh, row.CpuWh, row.GpuWh, row.DisplayWh, row.RestWh, row.IdleOnWh, row.IdleOffWh,
            row.OnS, row.BatteryS, row.IdleS, row.MeasuredS, row.CalibratedS, row.EstimatedS, row.CostMicro, row.Currency, row.Changed);
    }

    /// <summary>The rows with each change time no later than a day after <paramref name="nowMs"/> (plan 0.8): a PC whose clock
    /// is far ahead can't make a row that no later change replaces.</summary>
    public static List<HouseholdRow> CapChanged(IEnumerable<HouseholdRow> rows, long nowMs)
    {
        var cap = nowMs + (long)TimeSpan.FromDays(1).TotalMilliseconds;
        return [.. rows.Select(row => row.ChangedMs > cap ? row with { ChangedMs = cap } : row)];
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Hex32();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();
}
