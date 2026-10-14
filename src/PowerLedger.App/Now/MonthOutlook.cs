using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The month-to-date ledger (spec §9, Now screen). The daily average runs over the time since the first day recorded this
/// month, so a machine that joined mid-month is not averaged over days it was not watched, and a day it was off after
/// that counts as a day that used nothing; there is none until a full day has passed. The projection is what the month
/// has used so far plus that average for the days left (report finding 4), so days before the first one recorded add
/// nothing; its cost is what the month has cost so far plus the days left at today's tariff. The idle waste is priced at
/// the month's own average price, the energy having been used under whichever tariff was in force; it is counted on the
/// same scale as the energy that price is of, idle on battery at its wall equivalent (Plan AC review). Lowest and highest
/// are complete days only. A day of the month before that the month's hour rows reach into, in a zone half an hour off
/// UTC, is no day of this month.
/// </summary>
internal sealed record MonthOutlook(
    double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial, double MeasuredShare,
    double? DailyAverageKwh, double? ProjectedKwh, decimal? ProjectedCost,
    double IdleWasteKwh, decimal IdleWasteCost,
    DayTotals? Lowest, DayTotals? Highest, int DaysRecorded, DateOnly NextReport)
{
    /// <param name="tariff">The tariff in force now, which prices the days left; null without one, when the month's own
    /// average price stands in.</param>
    public static MonthOutlook From(RangeTotals month, IReadOnlyList<DayTotals> days, DateTimeOffset localNow, Tariff? tariff)
    {
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var first = new DateOnly(today.Year, today.Month, 1);
        var next = first.AddMonths(1);
        var inMonth = days.Where(d => d.Day >= first).ToList();
        var price = month.EnergyKwh > 0 ? month.Cost / (decimal)month.EnergyKwh : 0m;
        var todayPrice = tariff is { } t && t.Currency == month.Currency ? t.PricePerKwh : price;

        double? average = null;
        double? projected = null;
        decimal? projectedCost = null;
        if (inMonth.Count > 0)
        {
            var elapsed = (localNow.DateTime - inMonth[0].Day.ToDateTime(TimeOnly.MinValue)).TotalDays;
            if (elapsed >= 1)
            {
                var left = Math.Max(0, (next.ToDateTime(TimeOnly.MinValue) - localNow.DateTime).TotalDays);
                average = month.EnergyKwh / elapsed;
                projected = month.EnergyKwh + average * left;
                projectedCost = decimal.Round(month.Cost + (decimal)(average.Value * left) * todayPrice, 2);
            }
        }

        var complete = inMonth.Where(d => d.Day < today && d.OnHours > 0).ToList();
        var idle = month.IdleOnKwh + month.IdleOffKwh;
        return new MonthOutlook(
            month.EnergyKwh, month.Cost, month.Currency, month.CostIsPartial, month.MeasuredShare,
            average, projected, projectedCost,
            idle, decimal.Round((decimal)idle * price, 2),
            complete.MinBy(d => d.EnergyKwh), complete.MaxBy(d => d.EnergyKwh), inMonth.Count, next);
    }
}
