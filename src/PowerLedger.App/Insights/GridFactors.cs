using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerLedger.App;

/// <summary>A country's grid: grams of CO₂ per kWh generated, with where the figure comes from and its year.</summary>
/// <param name="Region">ISO 3166 two-letter code, upper-case.</param>
internal sealed record GridFactor(string Region, string Country, double Grams, string Source, int Year);

/// <summary>
/// The built-in grid factors (Aero look design §4): about 50 countries' carbon intensity of electricity generation, and
/// the world's. The App keeps one carbon setting, Settings' CO₂ per kWh; this table only supplies the figure Settings can
/// suggest for Windows' region, and lets the Carbon insight say where a figure came from.
/// <para>Every figure is Ember's for 2023 (Ember, Yearly Electricity Data and Global Electricity Review 2024, as published
/// by Our World in Data, "Carbon intensity of electricity generation", life-cycle gCO₂e per kWh), rounded to the gram, so
/// all of them were measured the same way in the same year.</para>
/// </summary>
internal static class GridFactors
{
    /// <summary>Settings keeps the factor in kg to two decimals, 10 g apart; a figure within half of that is the table's.</summary>
    public const double Match = 5;

    private const string Ember = "Ember";

    private const int Year = 2023;

    /// <summary>The world average, for a region the table doesn't hold.</summary>
    public static GridFactor World { get; } = new("001", "World", 481, Ember, Year);

    public static IReadOnlyList<GridFactor> All { get; } =
    [
        new("AR", "Argentina", 351, Ember, Year),
        new("AU", "Australia", 557, Ember, Year),
        new("AT", "Austria", 112, Ember, Year),
        new("BD", "Bangladesh", 683, Ember, Year),
        new("BE", "Belgium", 135, Ember, Year),
        new("BR", "Brazil", 96, Ember, Year),
        new("CA", "Canada", 174, Ember, Year),
        new("CL", "Chile", 304, Ember, Year),
        new("CN", "China", 583, Ember, Year),
        new("CO", "Colombia", 258, Ember, Year),
        new("CZ", "Czechia", 443, Ember, Year),
        new("DK", "Denmark", 152, Ember, Year),
        new("EG", "Egypt", 571, Ember, Year),
        new("FI", "Finland", 81, Ember, Year),
        new("FR", "France", 53, Ember, Year),
        new("DE", "Germany", 363, Ember, Year),
        new("GR", "Greece", 336, Ember, Year),
        new("HU", "Hungary", 196, Ember, Year),
        new("IN", "India", 713, Ember, Year),
        new("ID", "Indonesia", 681, Ember, Year),
        new("IE", "Ireland", 282, Ember, Year),
        new("IL", "Israel", 559, Ember, Year),
        new("IT", "Italy", 323, Ember, Year),
        new("JP", "Japan", 492, Ember, Year),
        new("MY", "Malaysia", 608, Ember, Year),
        new("MX", "Mexico", 489, Ember, Year),
        new("NL", "Netherlands", 268, Ember, Year),
        new("NZ", "New Zealand", 87, Ember, Year),
        new("NG", "Nigeria", 510, Ember, Year),
        new("NO", "Norway", 30, Ember, Year),
        new("PK", "Pakistan", 401, Ember, Year),
        new("PH", "the Philippines", 616, Ember, Year),
        new("PL", "Poland", 651, Ember, Year),
        new("PT", "Portugal", 158, Ember, Year),
        new("RO", "Romania", 243, Ember, Year),
        new("RU", "Russia", 443, Ember, Year),
        new("SA", "Saudi Arabia", 697, Ember, Year),
        new("SG", "Singapore", 501, Ember, Year),
        new("ZA", "South Africa", 714, Ember, Year),
        new("KR", "South Korea", 427, Ember, Year),
        new("ES", "Spain", 170, Ember, Year),
        new("SE", "Sweden", 38, Ember, Year),
        new("CH", "Switzerland", 35, Ember, Year),
        new("TH", "Thailand", 549, Ember, Year),
        new("TR", "Türkiye", 494, Ember, Year),
        new("UA", "Ukraine", 250, Ember, Year),
        new("AE", "the United Arab Emirates", 484, Ember, Year),
        new("GB", "the United Kingdom", 236, Ember, Year),
        new("US", "the United States", 393, Ember, Year),
        new("VN", "Vietnam", 472, Ember, Year),
    ];

    private static readonly Dictionary<string, GridFactor> ByRegion = All.ToDictionary(f => f.Region, StringComparer.OrdinalIgnoreCase);

    /// <summary>The grid of <paramref name="region"/> (ISO two-letter, any case); null for one the table doesn't hold.</summary>
    public static GridFactor? For(string? region) => region is not null && ByRegion.TryGetValue(region, out var factor) ? factor : null;

    /// <summary>What Settings can suggest: the region's figure, else the world's.</summary>
    public static GridFactor Suggested(string? region) => For(region) ?? World;

    /// <summary>
    /// Windows' region as Settings, Time and language, Region, Country or region has it (the user's home location, which
    /// is where the PC's electricity comes from), as an ISO two-letter code; else the region of the user's formats; null
    /// when neither names a country.
    /// </summary>
    public static string? WindowsRegion()
    {
        try
        {
            var buffer = new StringBuilder(16);
            if (GetUserDefaultGeoName(buffer, buffer.Capacity) > 1)
            {
                var name = buffer.ToString();
                if (name.Length == 2 && name.All(char.IsAsciiLetter)) return name.ToUpperInvariant();
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Before Windows 10 1709; the formats' region below stands in.
        }

        var formats = CultureInfo.CurrentCulture.Name;
        if (formats.Length == 0) return null;
        try
        {
            var region = new RegionInfo(formats).TwoLetterISORegionName;
            return region.Length == 2 && region.All(char.IsAsciiLetter) ? region.ToUpperInvariant() : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"GetUserDefaultGeoName function (winnls.h)": the home location as an ISO two-letter code, or a UN M.49
    /// number for a region that has none; the length written, its terminator included, or 0.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetUserDefaultGeoName(StringBuilder geoName, int geoNameCount);
}
