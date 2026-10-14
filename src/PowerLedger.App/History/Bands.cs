using System.Globalization;
using PowerLedger.Core;
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
            // What the PC drew asleep (accuracy A10), and what charging the battery lost at the wall for the energy it gave
            // (A9), are in the total but in none of the bands, which are at the pack, so each has its own line, with no
            // share, since the shares are of the bands as drawn.
            if (t.AsleepKwh > 0) rows.Add(new PartRow(null, "Asleep (estimated)", Format.Kwh(t.AsleepKwh, culture), "", 0));
            var losses = t.BatteryKwh * (1 - (1 / BatteryRoundTrip.Factor));
            if (losses > 0) rows.Add(new PartRow(null, "Battery charging losses", Format.Kwh(losses, culture), "", 0));
            rows.Add(new PartRow(null, "Total", Format.Kwh(t.EnergyKwh, culture), total > 0 ? Format.Percent(1, culture) : Format.Missing, total > 0 ? 1 : 0));
        }
        return rows;
    }
}
