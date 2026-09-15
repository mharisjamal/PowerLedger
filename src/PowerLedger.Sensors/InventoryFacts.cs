using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// What was detected about this machine (spec §5). The hash keys the learned calibration, so it covers only what
/// cannot change without opening the case: the chassis, the processor and the memory. Drives, displays and graphics
/// adapters come and go with docks, external drives and driver installs, and none of those may throw away a
/// learned baseline.
/// </summary>
/// <param name="GpuName">Not part of the hash: it reads "Microsoft Basic Display Adapter" until the vendor's driver installs.</param>
/// <param name="SsdCount">Not part of the hash, and neither is <paramref name="HddCount"/>: drives come and go.</param>
/// <param name="DisplayDiagonalInches">The built-in panel's diagonal, or 0 when none was found. Not part of the hash:
/// a laptop docked with its lid shut shows no panel at all.</param>
/// <param name="MonitorCount">How many displays were attached when this was detected. Not part of the hash.</param>
public sealed record InventoryFacts(
    ChassisKind Chassis,
    string? CpuName,
    string? GpuName,
    int RamSticks,
    bool RamIsDdr5,
    int SsdCount,
    int HddCount,
    double DisplayDiagonalInches,
    int MonitorCount)
{
    /// <summary>The processor's rated watts from the bundled table, or null when the model is unknown.</summary>
    public double? CpuTdpW => TdpTable.Bundled.Cpu(CpuName);

    /// <summary>The graphics card's rated watts from the bundled table, or null when the model is unknown.</summary>
    public double? GpuTdpW => TdpTable.Bundled.Gpu(GpuName);

    /// <summary>Stable across restarts, unlike a runtime string hash, so a stored calibration still matches.</summary>
    public string Hash
    {
        get
        {
            var identity = FormattableString.Invariant($"{Chassis}|{CpuName}|{RamSticks}|{RamIsDdr5}");
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexStringLower(digest.AsSpan(0, 8));
        }
    }

    /// <summary>The detected fields folded into the user's profile. Everything the user chose is left alone, and so is
    /// the panel size when no built-in panel was found, as on a laptop docked with its lid shut; a built-in panel that
    /// was found, an all-in-one's included, sets it. A laptop's panel size never passes to a machine now detected as a
    /// desktop, because a desktop counts any panel size as a built-in screen.</summary>
    public MachineProfile ToProfile(MachineProfile chosen) => chosen with
    {
        Chassis = Chassis,
        RamSticks = RamSticks,
        RamIsDdr5 = RamIsDdr5,
        SsdCount = SsdCount,
        HddCount = HddCount,
        DisplayDiagonalInches = DisplayDiagonalInches > 0 ? DisplayDiagonalInches
            : Chassis == ChassisKind.Desktop && chosen.Chassis == ChassisKind.Laptop ? 0
            : chosen.DisplayDiagonalInches,
    };

    /// <summary>The record as storage keeps it, for the status screen and for diagnosing a hash change.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);
}
