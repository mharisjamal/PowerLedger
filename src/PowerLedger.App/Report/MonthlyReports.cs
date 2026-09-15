using System.Globalization;
using System.IO;

namespace PowerLedger.App;

/// <summary>A monthly report the job wrote.</summary>
internal sealed record MonthlyReport(string Path, ReportData Data);

/// <summary>
/// The monthly report (spec §9). Soon after startup and every hour, each finished month of the last twelve, from the month
/// history began in, gets a PDF in Documents\PowerLedger if it has none yet, and the tray says so. A month with no readings
/// at all is skipped. The App does this because the service runs as SYSTEM and has no Documents folder.
/// </summary>
internal sealed class MonthlyReports : IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    public const int MonthsBack = 12;

    private readonly IRangeHistory _history;
    private readonly ISleepSettings _sleep;
    private readonly Func<ReportData, byte[]> _pdf;
    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly Action<IReadOnlyList<MonthlyReport>> _written;
    private readonly Lock _gate = new();
    private ITimer? _timer;

    public MonthlyReports(
        IRangeHistory history, ISleepSettings sleep, Func<ReportData, byte[]> pdf, string folder,
        TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, double co2KgPerKwh, Action<IReadOnlyList<MonthlyReport>> written)
    {
        _history = history;
        _sleep = sleep;
        _pdf = pdf;
        _folder = folder;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Co2KgPerKwh = co2KgPerKwh;
        _written = written;
    }

    public static string DefaultFolder { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerLedger");

    public static string FileName(DateOnly month) => $"PowerLedger-{month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.pdf";

    /// <summary>Kilograms of CO₂ per kWh, read at each check, so a change in Settings reaches the next report.</summary>
    public double Co2KgPerKwh { get; set; }

    /// <summary>Checks soon after startup and then every hour, on the timer's thread.</summary>
    public void Start() => _timer ??= _clock.CreateTimer(_ => CheckQuietly(), null, FirstCheck, CheckEvery);

    public void Dispose() => _timer?.Dispose();

    /// <summary>The first days of the months due, newest first: finished, in the last twelve, no earlier than the month
    /// history began in, and without a PDF.</summary>
    internal static IReadOnlyList<DateOnly> Due(DateOnly? firstDay, DateOnly today, Func<DateOnly, bool> exists)
    {
        if (firstDay is not { } first) return [];
        var start = new DateOnly(first.Year, first.Month, 1);
        var month = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var due = new List<DateOnly>();
        for (var i = 0; i < MonthsBack && month >= start; i++, month = month.AddMonths(-1))
        {
            if (!exists(month)) due.Add(month);
        }
        return due;
    }

    /// <summary>What the tray says about reports just written: the newest month's headline numbers, and how many there were.</summary>
    internal static (string Title, string Text) Toast(IReadOnlyList<MonthlyReport> written)
    {
        var newest = written[0].Data;
        var title = written.Count == 1 ? $"{newest.Title} report saved" : $"{written.Count.ToString(CultureInfo.InvariantCulture)} monthly reports saved";
        var cost = newest.Cost == Format.Missing ? "" : " · " + newest.Cost;
        return (title, $"{newest.Title}: {newest.Energy} kWh{cost}. Saved to Documents\\PowerLedger; click to open it.");
    }

    /// <summary>Writes the reports that are due and returns them, newest first. A check already running makes this one a no-op.</summary>
    internal IReadOnlyList<MonthlyReport> Check()
    {
        if (!_gate.TryEnter()) return [];
        try
        {
            var now = _clock.GetUtcNow();
            var written = new List<MonthlyReport>();
            foreach (var month in Due(_history.FirstDay(_zone), Ranges.LocalDay(now, _zone), m => File.Exists(PathOf(m))))
            {
                var range = Ranges.Month(month.Year, month.Month, now, _zone, _culture);
                if (_history.Read(range, _zone) is not { } report) break;                   // history can't be read: next hour
                var data = ReportData.From(report, _sleep.Read(), Co2KgPerKwh, _zone, _culture);
                if (!data.HasData) continue;                                                // nothing was recorded that month
                var path = PathOf(month);
                try
                {
                    Directory.CreateDirectory(_folder);
                    File.WriteAllBytes(path + ".partial", _pdf(data));
                    File.Move(path + ".partial", path, overwrite: true);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    break;                                                                  // Documents can't be written, or the PDF failed: next hour
                }
                written.Add(new MonthlyReport(path, data));
            }
            if (written.Count > 0) _written(written);
            return written;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private void CheckQuietly()
    {
        try
        {
            Check();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A timer thread has no one to tell; the next hour tries again.
        }
    }

    private string PathOf(DateOnly month) => System.IO.Path.Combine(_folder, FileName(month));
}
