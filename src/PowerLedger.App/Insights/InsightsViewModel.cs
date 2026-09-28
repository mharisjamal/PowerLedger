using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>
/// Aero's Insights page (Aero look design §4), as Task 0 fixes it for the shell and the views: <see cref="Report"/>, read
/// off the UI thread through <see cref="IInsights"/> as the page shows and every <see cref="RefreshEvery"/> while it does,
/// like the other pages' Show and Hide. Agent I adds what the views bind to beyond the report (the heatmap's cells, the
/// forecast band, the anomaly rows, the bell's alerts) without changing this surface.
/// </summary>
internal sealed class InsightsViewModel(IInsights insights, UiThreads threads, TimeProvider clock, TimeZoneInfo zone) : ObservableObject
{
    /// <summary>Hour rows change once an hour; a quarter of an hour keeps the findings fresh without reading for nothing.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    private InsightsReport? _report;
    private ITimer? _timer;
    private int _reading;

    /// <summary>The findings last read; null until the first read is back.</summary>
    public InsightsReport? Report { get => _report; private set => SetProperty(ref _report, value); }

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
                if (reading == _reading) Report = report;
            });
        });
    }
}
