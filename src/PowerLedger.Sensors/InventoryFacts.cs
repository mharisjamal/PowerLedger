using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// What was detected about this machine (spec §5). The hash keys the learned calibration, so it covers only what
/// cannot change without opening the case: the chassis, the processor and the memory. Drives, displays and graphics
/// adapters come and go with docks, external drives and driver installs, and none of those may throw away a
/// learned baseline.
/// </summary>
/// <param name="GpuName">Every graphics card, joined with <see cref="NameSeparator"/> when there are several, as "NVIDIA
/// Quadro 6000 + NVIDIA GeForce GTX 1080"; the processor's graphics when there is no card. One name, so the App and the
/// stored inventories before it read it as they always have. Not part of the hash: it reads "Microsoft Basic Display
/// Adapter" until the vendor's driver installs.</param>
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

    /// <summary>What joins the cards' names in <see cref="GpuName"/>.</summary>
    public const string NameSeparator = " + ";

    /// <summary>Each card's name on its own; none when nothing was named.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> GpuNames
        => string.IsNullOrWhiteSpace(GpuName) ? [] : GpuName.Split(NameSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The first card's rated watts from the bundled table, or null when the model is unknown. The model rates
    /// each card by its own name as the sensors read it; this is only the figure for a card they could not name.</summary>
    public double? GpuTdpW => GpuNames.Count > 0 ? TdpTable.Bundled.Gpu(GpuNames[0]) : null;

    /// <summary>True when the table knows none of the named graphics, or not every card, so a card's watts worked out from its
    /// load rest on a rough rating from its memory. Stored with the rest, where the App reads it to say so.</summary>
    public bool GpuTdpRough => GpuNames.Any(name => TdpTable.Bundled.Gpu(name) is null);

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
