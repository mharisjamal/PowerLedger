using System.Text.RegularExpressions;

namespace PowerLedger.Sensors;

/// <summary>
/// Tells an AMD or Intel graphics card apart from the graphics built into the processor, whose watts the processor's
/// own package reading already includes. The name decides, since AMD's processor graphics carry the Radeon name and
/// Intel's newest the Arc name, and memory of the card's own backs it up: processor graphics report only a carve-out
/// of system memory, usually 128 MB.
/// </summary>
public static partial class DiscreteGpu
{
    public const uint AmdVendor = 0x1002;
    public const uint IntelVendor = 0x8086;

    /// <summary>The least memory of its own an AMD or Intel card is counted with.</summary>
    public const ulong MinimumDedicatedBytes = 1UL << 30;

    /// <summary>True for an AMD Radeon RX, Radeon Pro or FirePro card, or an Intel Arc card with an A- or B-series model
    /// number. Processor graphics are not: "AMD Radeon(TM) Graphics", "Radeon 780M Graphics", "Radeon RX Vega 11
    /// Graphics" on the older Ryzens, and "Intel(R) Arc(TM) Graphics" or "Arc(TM) 140V GPU" on the newer Intel parts.</summary>
    public static bool IsDiscreteName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (AmdCard().IsMatch(name)) return !AmdProcessorGraphics().IsMatch(name);
        return IntelCard().IsMatch(name);
    }

    /// <summary>An adapter the GPU load source reads: AMD or Intel hardware, not a software rasteriser, with a card's
    /// name and at least <see cref="MinimumDedicatedBytes"/> of memory of its own.</summary>
    public static bool IsCandidate(GpuAdapter adapter)
        => adapter.VendorId is AmdVendor or IntelVendor
           && !adapter.Software
           && adapter.DedicatedBytes >= MinimumDedicatedBytes
           && IsDiscreteName(adapter.Description);

    /// <summary>The first candidate in the order Windows lists the adapters, or null when there is none.</summary>
    public static GpuAdapter? Choose(IEnumerable<GpuAdapter> adapters) => adapters.FirstOrDefault(IsCandidate);

    /// <summary>The graphics name the inventory records and the TDP is looked up by: an NVIDIA card first, then an AMD or
    /// Intel card, then whatever Windows lists first. Processor graphics come last because their watts are already
    /// inside the processor's.</summary>
    /// <param name="adapters">Each adapter's name and its vendor as Windows words it, e.g. "Advanced Micro Devices, Inc.".</param>
    public static string? PreferredName(IEnumerable<(string? Name, string? Vendor)> adapters)
    {
        string? card = null;
        string? first = null;
        foreach (var (name, vendor) in adapters)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (vendor?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true) return name;
            if (card is null && IsDiscreteName(name)) card = name;
            first ??= name;
        }
        return card ?? first;
    }

    /// <summary>"RX 7800 XT", "RX Vega", "Radeon Pro W7900", "Radeon(TM) PRO W6800", "FirePro W7100".</summary>
    [GeneratedRegex(@"\bRX\s|\bRadeon(\(TM\))?\s+Pro\b|\bFirePro\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdCard();

    /// <summary>The Ryzen 2000 and 3000 processors' graphics, e.g. "Radeon RX Vega 11 Graphics".</summary>
    [GeneratedRegex(@"\bVega\s+\d+\s+Graphics\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdProcessorGraphics();

    /// <summary>"Arc(TM) A770", "Arc B580", "Arc(TM) Pro A60": Arc followed by an A- or B-series model number.</summary>
    [GeneratedRegex(@"\bArc(\(TM\))?\s+(Pro\s+)?[AB]\d{2,3}", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCard();
}
