namespace PowerLedger.Sensors;

/// <summary>
/// The brand Energy Star's table lists a monitor under, from the three-letter maker code in the monitor's EDID. The codes
/// are those of the UEFI Forum's PNP ID registry; each brand is spelt as the table spells it, and the few makers the table
/// has no monitors from keep their own spelling.
/// </summary>
public static class MonitorMakers
{
    private static readonly Dictionary<string, string> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer",
        ["AOA"] = "AOpen",
        ["AOC"] = "AOC",
        ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ",
        ["DEL"] = "DELL",
        ["EIZ"] = "EIZO",       // not in the registry, which gives EIZO ENC
        ["ENC"] = "EIZO",
        ["ELO"] = "ELO",        // Elo TouchSystems
        ["GBT"] = "GIGABYTE",
        ["GSM"] = "LG",         // registered as Goldstar
        ["HKC"] = "HKC",
        ["HPN"] = "HP",         // HP Inc.
        ["HWP"] = "HP",         // Hewlett Packard
        ["IVM"] = "IIYAMA",
        ["LEN"] = "Lenovo",
        ["MSI"] = "MSI",        // registered as Microstep
        ["NEC"] = "NEC",
        ["PHL"] = "PHILIPS",
        ["SAM"] = "Samsung",
        ["SEC"] = "Samsung",    // registered to Seiko Epson, but Samsung's panels report it too
        ["SHP"] = "Sharp",
        ["SNY"] = "SONY",
        ["SPT"] = "SCEPTRE",
        ["VSC"] = "ViewSonic",
    };

    /// <summary>The table's brand for an EDID maker code such as "DEL", or null for a maker without one here.</summary>
    public static string? Brand(string? pnpId) => string.IsNullOrWhiteSpace(pnpId) ? null : Brands.GetValueOrDefault(pnpId.Trim());

    /// <summary>A brand's maker codes, which some monitors put in front of their names ("LEN T24i-10", "PHL 243V7").</summary>
    internal static IEnumerable<string> Codes(string brand)
        => Brands.Where(pair => pair.Value.Equals(brand, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key);
}
