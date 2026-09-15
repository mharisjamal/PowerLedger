using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>One line of a component table: a band, or the total, with its energy in kWh, its share, and the share as a fraction.</summary>
internal sealed record PartRow(Part? Part, string Name, string Energy, string Share, double Fraction);

/// <summary>The four bands of a range as the Breakdown table and the report list them.</summary>
internal static class Bands
{
    /// <summary>
    /// A row per band, and the total when asked. A negative rest, which measured mode shows when the parts over-report,
    /// counts as zero here as on every chart (spec §9), so the shares are of the four bands as drawn.
    /// </summary>
    public static IReadOnlyList<PartRow> Rows(RangeTotals t, CultureInfo culture, bool withTotal)
    {
        (Part Part, string Name, double Kwh)[] bands =
        [
            (Part.Cpu, "CPU package", t.CpuKwh), (Part.Gpu, "GPU", t.GpuKwh),
            (Part.Display, "Display", t.DisplayKwh), (Part.Rest, "Rest of system", t.RestKwh),
        ];
        var clean = bands.Select(b => (b.Part, b.Name, Kwh: double.IsFinite(b.Kwh) ? Math.Max(0, b.Kwh) : 0)).ToList();
        var total = clean.Sum(b => b.Kwh);
        var rows = clean.Select(b =>
        {
            var share = total > 0 ? b.Kwh / total : 0;
            return new PartRow(b.Part, b.Name, Format.Kwh(b.Kwh, culture), Format.Percent(share, culture), share);
        }).ToList();
        if (withTotal)
        {
            rows.Add(new PartRow(null, "Total", Format.Kwh(t.EnergyKwh, culture), total > 0 ? Format.Percent(1, culture) : Format.Missing, total > 0 ? 1 : 0));
        }
        return rows;
    }
}
