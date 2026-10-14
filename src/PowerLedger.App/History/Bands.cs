using System.Globalization;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>What a line of a component table is, for how it is drawn (Plan AC review): a band, with its colour; a line
/// beside the bands that is part of the total but of no band, what the PC drew asleep or what charging lost; or the total.</summary>
internal enum PartRowKind
{
    Band,
    Beside,
    Total,
}

/// <summary>One line of a component table: a band, a line beside the bands, or the total, with its energy in kWh, its
/// share, and the share as a fraction.</summary>
internal sealed record PartRow(Part? Part, string Name, string Energy, string Share, double Fraction, PartRowKind Kind = PartRowKind.Band);

/// <summary>The four bands of a range as the Breakdown table and the report list them.</summary>
internal static class Bands
{
    /// <summary>
    /// A row per band, and the total when asked. A negative rest, which measured mode shows when the parts over-report,
    /// counts as zero here as on every chart (spec §9), so the shares are of the four bands as drawn. With the total, what
    /// the PC drew asleep and what charging lost have lines of their own beside the bands, and every share is of the total
    /// the lines add up to (Plan AC review), which is then all of itself.
    /// </summary>
    public static IReadOnlyList<PartRow> Rows(RangeTotals t, CultureInfo culture, bool withTotal)
    {
        (Part Part, string Name, double Kwh)[] bands =
        [
            (Part.Cpu, "CPU package", t.CpuKwh), (Part.Gpu, "GPU", t.GpuKwh),
            (Part.Display, "Display", t.DisplayKwh), (Part.Rest, "Rest of system", t.RestKwh),
        ];
        var clean = bands.Select(b => (b.Part, b.Name, Kwh: Clean(b.Kwh))).ToList();
        // What the PC drew asleep (accuracy A10), and what charging the battery lost at the wall for the energy it gave
        // (A9), are in the total but in none of the bands, which are at the pack.
        var asleep = withTotal ? Clean(t.AsleepKwh) : 0;
        var losses = withTotal ? Clean(t.BatteryKwh * (1 - (1 / BatteryRoundTrip.Factor))) : 0;
        var whole = clean.Sum(b => b.Kwh) + asleep + losses;
        PartRow Line(Part? part, string name, double kwh, PartRowKind kind)
        {
            var share = whole > 0 ? kwh / whole : 0;
            return new PartRow(part, name, Format.Kwh(kwh, culture), Format.Percent(share, culture), share, kind);
        }

        var rows = clean.Select(b => Line(b.Part, b.Name, b.Kwh, PartRowKind.Band)).ToList();
        if (withTotal)
        {
            if (asleep > 0) rows.Add(Line(null, "Asleep (estimated)", asleep, PartRowKind.Beside));
            if (losses > 0) rows.Add(Line(null, "Battery charging losses", losses, PartRowKind.Beside));
            rows.Add(new PartRow(
                null, "Total", Format.Kwh(t.EnergyKwh, culture), whole > 0 ? Format.Percent(1, culture) : Format.Missing, whole > 0 ? 1 : 0, PartRowKind.Total));
        }
        return rows;
    }

    private static double Clean(double kwh) => double.IsFinite(kwh) ? Math.Max(0, kwh) : 0;
}
