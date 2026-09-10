using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// What was detected about this machine (spec §5). The hash keys the learned calibration, so it covers the
/// parts whose power draw is fixed and deliberately excludes the monitor count, which changes when someone
/// plugs in a screen and must not throw away a learned baseline.
/// </summary>
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
            var identity = string.Join('|',
                Chassis, CpuName ?? "", GpuName ?? "", RamSticks, RamIsDdr5, SsdCount, HddCount,
                DisplayDiagonalInches.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexStringLower(digest.AsSpan(0, 8));
        }
    }

    /// <summary>The detected fields folded into the user's profile; everything the user chose is left alone.</summary>
    public MachineProfile ToProfile(MachineProfile chosen) => chosen with
    {
        Chassis = Chassis,
        RamSticks = RamSticks,
        RamIsDdr5 = RamIsDdr5,
        SsdCount = SsdCount,
        HddCount = HddCount,
        DisplayDiagonalInches = DisplayDiagonalInches,
    };

    /// <summary>The record as storage keeps it, for the status screen and for diagnosing a hash change.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);
}
