using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The month-to-date ledger (spec §9, Now screen). The daily average runs over the days recorded, from the first of them,
/// so a machine that joined mid-month is not averaged over days it was not watched; there is none until a full day has
/// passed. The projection and the idle waste are priced at the month's own average price, which follows any tariff
/// change. Lowest and highest are complete days only.
/// </summary>
internal sealed record MonthOutlook(
    double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial, double MeasuredShare,
    double? DailyAverageKwh, double? ProjectedKwh, decimal? ProjectedCost,
    double IdleWasteKwh, decimal IdleWasteCost,
    DayTotals? Lowest, DayTotals? Highest, int DaysRecorded, DateOnly NextReport)
{
    public static MonthOutlook From(RangeTotals month, IReadOnlyList<DayTotals> days, DateTimeOffset localNow)
    {
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var price = month.EnergyKwh > 0 ? month.Cost / (decimal)month.EnergyKwh : 0m;

        double? average = null;
        double? projected = null;
        decimal? projectedCost = null;
        if (days.Count > 0)
        {
            var elapsed = (localNow.DateTime - days[0].Day.ToDateTime(TimeOnly.MinValue)).TotalDays;
            if (elapsed >= 1)
            {
                average = month.EnergyKwh / elapsed;
                projected = average * DateTime.DaysInMonth(today.Year, today.Month);
                projectedCost = decimal.Round((decimal)projected.Value * price, 2);
            }
        }

        var complete = days.Where(d => d.Day < today && d.OnHours > 0).ToList();
        var idle = month.IdleOnKwh + month.IdleOffKwh;
        return new MonthOutlook(
            month.EnergyKwh, month.Cost, month.Currency, month.CostIsPartial, month.MeasuredShare,
            average, projected, projectedCost,
            idle, decimal.Round((decimal)idle * price, 2),
            complete.MinBy(d => d.EnergyKwh), complete.MaxBy(d => d.EnergyKwh), days.Count,
            new DateOnly(today.Year, today.Month, 1).AddMonths(1));
    }
}
