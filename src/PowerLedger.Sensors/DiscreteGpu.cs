using System.Globalization;
using System.Text.RegularExpressions;

namespace PowerLedger.Sensors;

/// <summary>A display adapter as WMI's Win32_VideoController lists it, which asks nothing of the card itself.</summary>
/// <param name="Name">The adapter's name, e.g. "NVIDIA Quadro 6000".</param>
/// <param name="Vendor">AdapterCompatibility, the maker as Windows words it, e.g. "Advanced Micro Devices, Inc.".</param>
/// <param name="PnpDeviceId">The Plug and Play instance id, e.g. "PCI\VEN_10DE&amp;DEV_06D8&amp;...".</param>
/// <param name="AdapterRam">The memory WMI reports, which it caps at 4 GB; 0 when it gave none.</param>
public sealed record VideoController(string? Name, string? Vendor, string? PnpDeviceId = null, ulong AdapterRam = 0);

/// <summary>
/// Tells a graphics card apart from the graphics built into the processor, whose watts the processor's own package
/// reading already includes. Only AMD and Intel put graphics in a Windows PC's processor, so for their adapters the name
/// decides, since AMD's processor graphics carry the Radeon name and Intel's newest the Arc name, and memory of the card's
/// own backs it up: processor graphics report only a carve-out of system memory, usually 128 MB. NVIDIA's adapters, and
/// any other maker's hardware with a card's memory, are cards.
/// </summary>
public static partial class DiscreteGpu
{
    public const uint AmdVendor = 0x1002;
    public const uint IntelVendor = 0x8086;
    public const uint NvidiaVendor = 0x10DE;

    /// <summary>Microsoft's own adapters: the Basic Render and Basic Display drivers, Hyper-V's and Remote Desktop's.</summary>
    public const uint MicrosoftVendor = 0x1414;

    /// <summary>Qualcomm's graphics on an Arm PC, which are in the processor: its ACPI vendor id "QCOM", and its PCI id.</summary>
    private const uint QualcommVendor = 0x4D4F4351;
    private const uint QualcommPciVendor = 0x5143;

    /// <summary>The least memory of its own a card is counted with.</summary>
    public const ulong MinimumDedicatedBytes = 1UL << 30;

    /// <summary>True for an AMD Radeon RX, R9, numbered R7, Radeon VII, Radeon Pro or FirePro card, a Radeon HD 5000 to
    /// 8000 card, or an Intel Arc card with an A- or B-series model number. Processor graphics are not: "AMD Radeon(TM)
    /// Graphics", "Radeon 780M Graphics", "Radeon RX Vega 11 Graphics" on the older Ryzens, "Radeon R7 Graphics" and the
    /// "HD 7660D" or "HD 6310 Graphics" of the APUs before them, and "Intel(R) Arc(TM) Graphics" or "Arc(TM) 140V GPU" on
    /// the newer Intel parts.</summary>
    public static bool IsDiscreteName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (AmdCard().IsMatch(name)) return !AmdProcessorGraphics().IsMatch(name);
        if (AmdHdCard().Match(name) is { Success: true } hd)
        {
            // An APU's graphics end in D (desktop) or G (laptop), or are called "Graphics".
            return hd.Groups["suffix"].Value.ToUpperInvariant() is not ("D" or "G")
                   && !name.Contains("Graphics", StringComparison.OrdinalIgnoreCase);
        }
        return IntelCard().IsMatch(name);
    }

    /// <summary>An adapter the GPU load source reads: hardware, not a software rasteriser, with at least
    /// <see cref="MinimumDedicatedBytes"/> of memory of its own; for AMD and Intel a card's name as well, and never one of
    /// Microsoft's own adapters or an Arm PC's processor graphics.</summary>
    public static bool IsCandidate(GpuAdapter adapter)
        => !adapter.Software
           && adapter.DedicatedBytes >= MinimumDedicatedBytes
           && adapter.VendorId switch
           {
               AmdVendor or IntelVendor => IsDiscreteName(adapter.Description),
               MicrosoftVendor or QualcommVendor or QualcommPciVendor => false,
               _ => true,
           };

    /// <summary>Every candidate, in the order Windows lists the adapters.</summary>
    public static IReadOnlyList<GpuAdapter> Candidates(IEnumerable<GpuAdapter> adapters) => [.. adapters.Where(IsCandidate)];

    /// <summary>A card by what WMI says of it: an NVIDIA adapter; an AMD or Intel one with a card's name; or another maker's
    /// hardware with a card's memory. Never an adapter that is not on the PCI bus, as a remote, virtual or USB display is,
    /// nor Windows' own Basic Display Adapter standing in on a card whose driver is not installed.</summary>
    public static bool IsCard(VideoController controller)
    {
        if (string.IsNullOrWhiteSpace(controller.Name)) return false;
        if (controller.Name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)) return false;
        if (controller.PnpDeviceId is { Length: > 0 } id && !id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) return false;
        return VendorOf(controller) switch
        {
            NvidiaVendor => true,
            AmdVendor or IntelVendor => IsDiscreteName(controller.Name),
            0 or MicrosoftVendor or QualcommPciVendor => false,
            _ => controller.AdapterRam >= MinimumDedicatedBytes,
        };
    }

    /// <summary>True when WMI lists a card that is not NVIDIA's, which NVML cannot read: an AMD or Intel card, or another
    /// maker's. Then the other makers' libraries and Windows' load counters are wanted beside NVIDIA's.</summary>
    public static bool HasCardBesideNvidia(IEnumerable<VideoController> controllers)
        => controllers.Any(controller => IsCard(controller) && VendorOf(controller) != NvidiaVendor);

    /// <summary>The display adapters WMI lists, or null when it will not answer.</summary>
    public static IReadOnlyList<VideoController>? ReadVideoControllers()
        => Wmi.ReadOr<IReadOnlyList<VideoController>?>(@"\\.\root\cimv2", "SELECT Name, AdapterCompatibility, PNPDeviceID, AdapterRAM FROM Win32_VideoController", rows =>
            [.. rows.Select(row => new VideoController(
                (row["Name"] as string)?.Trim(), row["AdapterCompatibility"] as string, row["PNPDeviceID"] as string,
                row["AdapterRAM"] is { } ram ? Convert.ToUInt64(ram, CultureInfo.InvariantCulture) : 0))], null);

    /// <summary>The PCI vendor in a Plug and Play id such as "PCI\VEN_10DE&amp;DEV_06D8", or 0 when it has none.</summary>
    public static uint VendorIdIn(string? pnpId) => Hex(VendorPart(), pnpId);

    /// <summary>The PCI device id in a Plug and Play id such as "PCI\VEN_10DE&amp;DEV_06D8", or 0 when it has none.</summary>
    public static uint DeviceIdIn(string? pnpId) => Hex(DevicePart(), pnpId);

    /// <summary>The graphics name the inventory records: every card, NVIDIA's first and then the rest in the order Windows
    /// lists them, joined with <see cref="InventoryFacts.NameSeparator"/>, as "NVIDIA Quadro 6000 + NVIDIA GeForce GTX
    /// 1080"; or, with no card, <see cref="PreferredName"/>'s choice.</summary>
    public static string? InventoryName(IReadOnlyList<VideoController> controllers)
    {
        var cards = controllers.Where(IsCard).OrderBy(card => VendorOf(card) == NvidiaVendor ? 0 : 1).Select(card => card.Name!).ToList();
        return cards.Count > 0
            ? string.Join(InventoryFacts.NameSeparator, cards)
            : PreferredName(controllers.Select(controller => (controller.Name, controller.Vendor)));
    }

    /// <summary>The graphics name the inventory records when there is no card, and the one it recorded before every card
    /// was: an NVIDIA card first, then an AMD or Intel card, then whatever Windows lists first. Processor graphics come
    /// last because their watts are already inside the processor's.</summary>
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

    /// <summary>The maker by the adapter's PCI id, or by how Windows words its name when there is no id.</summary>
    private static uint VendorOf(VideoController controller)
    {
        if (VendorIdIn(controller.PnpDeviceId) is not 0 and var id) return id;
        var vendor = controller.Vendor ?? "";
        if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return NvidiaVendor;
        if (vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
            || vendor.StartsWith("ATI ", StringComparison.OrdinalIgnoreCase)
            || vendor.StartsWith("AMD", StringComparison.OrdinalIgnoreCase)) return AmdVendor;
        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return IntelVendor;
        return 0;
    }

    private static uint Hex(Regex part, string? pnpId)
        => pnpId is not null && part.Match(pnpId) is { Success: true } found
            ? uint.Parse(found.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>"RX 7800 XT", "RX Vega", "Radeon Pro W7900", "Radeon(TM) PRO W6800", "FirePro W7100", "R9 290X",
    /// "R9 200 Series", "R9 Fury Series", "R7 370", "R7 M260", "Radeon VII".</summary>
    [GeneratedRegex(@"\bRX\s|\bRadeon(\(TM\))?\s+Pro\b|\bFirePro\b|\bR9\s|\bR7\s+M?\d{3}\b|\bRadeon\s+VII\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdCard();

    /// <summary>"HD 7970", "HD 7970M", "HD 5800 Series", and the APUs' "HD 7660D" and "HD 7660G".</summary>
    [GeneratedRegex(@"\bHD\s*[5-8]\d{3}(?<suffix>[A-Z]{0,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdHdCard();

    /// <summary>The Ryzen 2000 and 3000 processors' graphics, e.g. "Radeon RX Vega 11 Graphics".</summary>
    [GeneratedRegex(@"\bVega\s+\d+\s+Graphics\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmdProcessorGraphics();

    /// <summary>"Arc(TM) A770", "Arc B580", "Arc(TM) Pro A60": Arc followed by an A- or B-series model number.</summary>
    [GeneratedRegex(@"\bArc(\(TM\))?\s+(Pro\s+)?[AB]\d{2,3}", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCard();

    [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorPart();

    [GeneratedRegex(@"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DevicePart();
}
