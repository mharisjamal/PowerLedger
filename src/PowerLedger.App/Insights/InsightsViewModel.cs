using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>
/// Aero's Insights page (Aero look design §4), as Task 0 fixes it for the shell and the views: <see cref="Report"/>, read
/// off the UI thread through <see cref="IInsights"/> as the page shows and every <see cref="RefreshEvery"/> while it does,
/// like the other pages' Show and Hide. From each report it words the four cards the page binds to, each with the empty
/// state design §6 gives it, and the bell's <see cref="Alerts"/>.
/// </summary>
internal sealed class InsightsViewModel(IInsights insights, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo? culture = null)
    : ObservableObject
{
    /// <summary>Hour rows change once an hour; a quarter of an hour keeps the findings fresh without reading for nothing.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    private const string Reading = "Reading the history";
    private const string NeedsAWeek = "Needs a week of data";

    private readonly CultureInfo _culture = culture ?? CultureInfo.CurrentCulture;
    private InsightsReport? _report;
    private ITimer? _timer;
    private int _reading;

    /// <summary>The findings last read; null until the first read is back.</summary>
    public InsightsReport? Report { get => _report; private set => SetProperty(ref _report, value); }

    public ForecastCard Forecast { get; private set; } = new(false, Reading, "", "", 0, 0, 0, 1);

    public IReadOnlyList<AnomalyRow> Unusual { get; private set; } = [];

    /// <summary>What the unusual hours come to, or that there are none.</summary>
    public string UnusualNote { get; private set; } = Reading;

    /// <summary>Today's unusual hours for the bell, newest first, at most three (design §4).</summary>
    public IReadOnlyList<UsageAnomaly> Alerts { get; private set; } = [];

    public HabitsCard Habits { get; private set; } = new(false, null, 0, 0, Reading, "");

    public CarbonCard Carbon { get; private set; } = new(Format.Missing, Format.Missing, "", "");

    /// <summary>The page is shown: read now, and every <see cref="RefreshEvery"/> until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= clock.CreateTimer(_ => threads.Post(Refresh), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads off the UI thread; a read a newer one overtook is dropped. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var reading = ++_reading;
        var now = clock.GetUtcNow();
        threads.Background(() =>
        {
            var report = insights.Read(now, zone);
            threads.Post(() =>
            {
                if (reading != _reading) return;
                Report = report;
                Word(report, now);
            });
        });
    }

    private void Word(InsightsReport report, DateTimeOffset now)
    {
        Forecast = ForecastOf(report.Forecast, now);
        var maximum = report.Anomalies.Select(a => Math.Max(a.Kwh, a.NormalKwh)).DefaultIfEmpty(0).Max();
        Unusual = [.. report.Anomalies.Select(a => RowOf(a, maximum))];
        UnusualNote = report.Anomalies.Count switch
        {
            0 => "Nothing unusual this week",
            1 => "1 hour this week used far more than usual",
            var count => $"{count.ToString(_culture)} hours this week used far more than usual",
        };
        Alerts = UsageAnomalies.Today(report.Anomalies, now, zone);
        Habits = HabitsOf(report.Habits);
        Carbon = new CarbonCard(
            Format.Kg(report.Carbon.MonthKg, _culture) + " kg", Format.Kg(report.Carbon.SinceStartKg, _culture) + " kg",
            Format.WholeWatts(report.Carbon.GramsPerKwh, _culture) + " g CO₂ per kWh", report.Carbon.Source);
        foreach (var name in new[] { nameof(Forecast), nameof(Unusual), nameof(UnusualNote), nameof(Alerts), nameof(Habits), nameof(Carbon) })
            OnPropertyChanged(name);
    }

    private ForecastCard ForecastOf(BillForecast forecast, DateTimeOffset now)
    {
        if (!forecast.Ready)
            return new(false, NeedsAWeek, "", $"{forecast.DaysOfData.ToString(_culture)} of {BillForecast.DaysNeeded.ToString(_culture)} days so far", 0, 0, 0, 1);
        if (forecast.Currency is not { } currency) return new(false, "No tariff set", "", "Set a tariff in Settings to see the likely bill", 0, 0, 0, 1);

        var month = TimeZoneInfo.ConvertTime(now, zone).ToString("MMMM", _culture);
        return new ForecastCard(
            true, Money.Format(forecast.ProjectedCost, currency, _culture),
            $"Likely {Money.Format(forecast.Low, currency, _culture)} to {Money.Format(forecast.High, currency, _culture)}",
            $"By the end of {month}, from {forecast.DaysOfData.ToString(_culture)} days of history",
            (double)forecast.Low, (double)forecast.ProjectedCost, (double)forecast.High, Math.Max(0.01, (double)forecast.High * 1.2));
    }

    private AnomalyRow RowOf(UsageAnomaly anomaly, double maximum)
    {
        var hour = TimeZoneInfo.ConvertTime(anomaly.Hour, zone);
        var end = TimeZoneInfo.ConvertTime(anomaly.Hour + TimeSpan.FromHours(1), zone);
        var when = $"{hour.ToString("ddd d MMM", _culture)}, {hour.ToString("HH:mm", _culture)} to {end.ToString("HH:mm", _culture)}";
        var times = double.IsFinite(anomaly.Times) ? $"{anomaly.Times.ToString("0.0", _culture)} times normal" : "Normally off";
        var normal = anomaly.NormalKwh > 0 ? "Normally " + Format.Kwh(anomaly.NormalKwh, _culture) + " kWh" : "";
        return new AnomalyRow(when, Format.Kwh(anomaly.Kwh, _culture) + " kWh", normal, times,
            anomaly.Kwh, anomaly.NormalKwh, maximum);
    }

    private HabitsCard HabitsOf(IdleHabits? habits)
    {
        if (habits is null) return new(false, null, 0, 0, NeedsAWeek, "");
        if (!habits.Heatmap.Cast<double>().Any(wh => wh > 0)) return new(true, habits.Heatmap, 0, 0, "No idle time yet", "");

        var from = Clock(habits.WorstWindowStart);
        var to = Clock((habits.WorstWindowStart + habits.WorstWindowHours) % 24);
        var cost = habits.Currency is { } currency && habits.SavingPerMonthCost > 0 ? $" ({Money.Format(habits.SavingPerMonthCost, currency, _culture)})" : "";
        var saving = habits.SavingPerMonthKwh >= 0.005
            ? $"Sleeping after {IdleAdvice.Span(HabitsFinder.SleepAfter)} idle would save about {Format.Kwh(habits.SavingPerMonthKwh, _culture)} kWh{cost} a month."
            : "Idle time used little energy in these four weeks.";
        return new HabitsCard(true, habits.Heatmap, habits.WorstWindowStart, habits.WorstWindowHours, $"Idle time costs most from {from} to {to}", saving);
    }

    private string Clock(int hour) => new TimeOnly(hour, 0).ToString("HH:mm", _culture);
}
