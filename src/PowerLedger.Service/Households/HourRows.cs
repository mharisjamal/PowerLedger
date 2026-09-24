using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>
/// This PC's hour rows for the household (households design §1): one per complete hour in samples_1h, with the energy by
/// part, the idle energy and seconds, how the figures were known, and the cost at the tariff in force at the hour's start,
/// in millionths of that tariff's currency. A row is written only when its figures differ from the one kept, and then as
/// changed now, so a later correction of an hour, or a tariff entered for the past, replaces it on every member. Now is
/// never earlier than just after this PC's newest change: after the clock goes back, what is built still counts as newer
/// than everything before it, and goes to the other members. A change more than a day ahead, left by a clock that ran
/// fast, goes back to now once (plan 0.9), since every member takes one as no later than a day from its own now.
/// </summary>
internal sealed class HourRows(AggregateRepository aggregates, TariffRepository tariffs, HouseholdRepository household)
{
    /// <summary>Where building starts: on joining, and every hour, the rows reach back 13 months, so the household sees the
    /// year and a tariff entered for the past reprices it.</summary>
    public static DateTimeOffset BackfillFrom(DateTimeOffset now) => now.AddMonths(-13);

    /// <summary>Builds the rows for every hour in samples_1h from <paramref name="from"/> to <paramref name="now"/>, and writes
    /// the ones whose figures changed.</summary>
    /// <returns>How many rows were written.</returns>
    public int Build(string deviceId, DateTimeOffset from, DateTimeOffset now)
    {
        var schedule = tariffs.Schedule();
        var nowMs = now.ToUnixTimeMilliseconds();
        var changedMs = Math.Max(nowMs, household.LatestChange(deviceId) + 1);   // the clock may have gone back
        var kept = household.RowsBetween(deviceId, from.ToUnixTimeMilliseconds(), nowMs).ToDictionary(row => row.HourMs);
        var changed = new List<HouseholdRow>();
        foreach (var hour in aggregates.ReadHours(from, now))
        {
            var row = Of(deviceId, hour, schedule.At(hour.Start), changedMs);
            if (kept.TryGetValue(row.HourMs, out var before) && before.SameFigures(row)) continue;
            changed.Add(row);
        }
        return changed.Count == 0 ? 0 : household.Upsert(changed);
    }

    /// <summary>Moves this PC's change times back to now when the newest is more than a day ahead of it (plan 0.9).</summary>
    /// <returns>True when they moved.</returns>
    public bool Rebase(string deviceId, DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        if (household.LatestChange(deviceId) <= nowMs + (long)TimeSpan.FromDays(1).TotalMilliseconds) return false;
        household.Rebase(deviceId, nowMs);
        return true;
    }

    /// <summary>One hour's row, costed at <paramref name="tariff"/>, or with no cost when there is none.</summary>
    internal static HouseholdRow Of(string deviceId, Aggregate hour, Tariff? tariff, long changedMs) => new(
        deviceId, hour.Start.ToUnixTimeMilliseconds(),
        hour.EnergyWh, hour.CpuWh, hour.GpuWh, hour.DisplayWh, hour.RestWh, hour.IdleOnWh, hour.IdleOffWh,
        hour.OnSeconds, hour.BatterySeconds, hour.IdleOnSeconds + hour.IdleOffSeconds,
        hour.MeasuredSeconds, hour.CalibratedSeconds, hour.EstimatedSeconds,
        tariff is null ? null : CostMicro(hour.EnergyWh, tariff.PricePerKwh), tariff?.Currency, changedMs);

    /// <summary>Watt-hours at a price per kWh, in millionths, halves rounded away from zero.</summary>
    internal static long CostMicro(double energyWh, decimal pricePerKwh) =>
        double.IsFinite(energyWh) ? (long)decimal.Round((decimal)energyWh * pricePerKwh * 1000m, MidpointRounding.AwayFromZero) : 0;
}
