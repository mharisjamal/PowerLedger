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
        foreach (var hour in LaidBack(aggregates.ReadHours(from, now), from))
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

    /// <summary>One hour's row, costed at <paramref name="tariff"/>, or with no cost when there is none. Its energy and cost are
    /// the hour's as the PC's own report counts them (<see cref="Aggregate.CountedWh"/>); its parts are the PC's awake.</summary>
    internal static HouseholdRow Of(string deviceId, Aggregate hour, Tariff? tariff, long changedMs) => new(
        deviceId, hour.Start.ToUnixTimeMilliseconds(),
        hour.CountedWh, hour.CpuWh, hour.GpuWh, hour.DisplayWh, hour.RestWh, hour.IdleOnWh, hour.IdleOffWh,
        hour.OnSeconds, hour.BatterySeconds, hour.IdleOnSeconds + hour.IdleOffSeconds,
        hour.MeasuredSeconds, hour.CalibratedSeconds, hour.EstimatedSeconds,
        tariff is null ? null : CostMicro(hour.CountedWh, tariff.PricePerKwh), tariff?.Currency, changedMs);

    /// <summary>
    /// The hours with what the PC drew asleep laid back over the hours it slept (Plan AC review), as the PC's own report lays
    /// it: a sleep is stored in the hour the PC woke in, and is spread evenly back from the wake over its gap, so each
    /// hour's energy is its parts and its own sleep. An hour slept through, which has no row of its own, gets one with its
    /// sleep alone. Sleep from before the first hour from <paramref name="from"/> is left out, as the report leaves out
    /// sleep from before its range.
    /// </summary>
    internal static List<Aggregate> LaidBack(IReadOnlyList<Aggregate> hours, DateTimeOffset from)
    {
        var first = new DateTimeOffset(from.UtcTicks + ((Hour.Ticks - (from.UtcTicks % Hour.Ticks)) % Hour.Ticks), TimeSpan.Zero);
        var laid = hours.ToDictionary(hour => hour.Start, hour => hour with { AsleepWh = 0 });
        foreach (var hour in hours)
        {
            if (hour.AsleepWh <= 0) continue;
            if (hour.GapSeconds <= 0)
            {
                laid[hour.Start] = laid[hour.Start] with { AsleepWh = laid[hour.Start].AsleepWh + hour.AsleepWh };
                continue;
            }
            var perSecond = hour.AsleepWh / hour.GapSeconds;
            var left = hour.GapSeconds;
            var cursor = hour.Start + Hour - TimeSpan.FromSeconds(Math.Min(hour.OnSeconds, Hour.TotalSeconds));   // the wake
            while (left > 0 && cursor > first)
            {
                var start = new DateTimeOffset((cursor.UtcTicks - 1) - ((cursor.UtcTicks - 1) % Hour.Ticks), TimeSpan.Zero);
                var taken = Math.Min(left, (cursor - start).TotalSeconds);
                var into = laid.GetValueOrDefault(start) ?? Aggregate.Empty(start);
                laid[start] = into with { AsleepWh = into.AsleepWh + (taken * perSecond) };
                left -= taken;
                cursor = start;
            }
        }
        return [.. laid.Values.OrderBy(hour => hour.Start)];
    }

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Watt-hours at a price per kWh, in millionths, halves rounded away from zero.</summary>
    internal static long CostMicro(double energyWh, decimal pricePerKwh) =>
        double.IsFinite(energyWh) ? (long)decimal.Round((decimal)energyWh * pricePerKwh * 1000m, MidpointRounding.AwayFromZero) : 0;
}
