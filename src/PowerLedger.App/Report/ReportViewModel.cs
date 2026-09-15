using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>
/// The Report screen (spec §9): the energy bill for a range, idle waste and the saving suggestion, everyday equivalents,
/// the quality mix and daily bars, and the PDF, CSV and PNG exports. It reads when shown, when the range changes and every
/// minute while shown, off the UI thread. Exports ask where to save on the UI thread and write off it, except the picture,
/// which the view draws on the UI thread.
/// </summary>
internal sealed class ReportViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly IRangeHistory _history;
    private readonly ISleepSettings _sleep;
    private readonly IFileSaver _saver;
    private readonly Func<ReportData, byte[]> _pdf;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private double _co2KgPerKwh;
    private ITimer? _timer;
    private int _reads;
    private DateRange? _range;
    private ReportData _data = ReportData.Empty;
    private string? _message;
    private string? _saved;

    public ReportViewModel(
        IRangeHistory history, ISleepSettings sleep, IFileSaver saver, Func<ReportData, byte[]> pdf,
        UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, double co2KgPerKwh)
    {
        _history = history;
        _sleep = sleep;
        _saver = saver;
        _pdf = pdf;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2KgPerKwh = co2KgPerKwh;
        Range = new RangePicker(RangeChoice.ThisMonth, Ranges.LocalDay(clock.GetUtcNow(), zone));
        Range.Changed += Refresh;
        ExportPdf = new RelayCommand(SavePdf);
        ExportCsv = new RelayCommand<string>(SaveCsv);
    }

    public RangePicker Range { get; }

    public ReportData Data { get => _data; private set => SetProperty(ref _data, value); }

    /// <summary>Why there is nothing to show, or null.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>What the last export did: the file it saved, or why it could not.</summary>
    public string? Saved { get => _saved; private set => SetProperty(ref _saved, value); }

    public ICommand ExportPdf { get; }

    /// <summary>Takes the grain's name: Raw, Minute or Hour.</summary>
    public ICommand ExportCsv { get; }

    /// <summary>Kilograms of CO₂ per kWh; a new factor reads the report again.</summary>
    public double Co2KgPerKwh
    {
        get => _co2KgPerKwh;
        set
        {
            _co2KgPerKwh = value;
            if (_range is not null) Refresh();
        }
    }

    /// <summary>The page is shown: read now, and every minute until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(Refresh), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads the chosen range off the UI thread; a read a newer one overtook is dropped. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var read = ++_reads;
        var range = Range.Resolve(_clock.GetUtcNow(), _zone, _culture);
        _threads.Background(() =>
        {
            var report = _history.Read(range, _zone);
            var data = report is null ? null : ReportData.From(report, _sleep.Read(), _co2KgPerKwh, _zone, _culture);
            _threads.Post(() =>
            {
                if (read != _reads) return;
                _range = range;
                Data = data ?? ReportData.Empty with { Title = range.Title };
                Message = data is null ? "History can't be read right now. It comes back when the service is running."
                    : data.HasData ? null : "No readings in this range.";
            });
        });
    }

    /// <summary>"PNG": asks where to save, then lets the view draw the report into the file. Call on the UI thread.</summary>
    public void SaveImage(Action<Stream> draw) => Save("", "png", "PNG image|*.png", draw, background: false);

    public void Dispose()
    {
        Range.Changed -= Refresh;
        Hide();
    }

    /// <summary>A finished month is "PowerLedger-2026-08", a day "PowerLedger-2026-09-08", anything else its first and last days.</summary>
    internal static string FileName(DateRange range, TimeZoneInfo zone)
    {
        var (first, last) = Ranges.Covered(range, zone);
        var invariant = CultureInfo.InvariantCulture;
        if (first.Day == 1 && last == first.AddMonths(1).AddDays(-1)) return "PowerLedger-" + first.ToString("yyyy-MM", invariant);
        if (first == last) return "PowerLedger-" + first.ToString("yyyy-MM-dd", invariant);
        return $"PowerLedger-{first.ToString("yyyy-MM-dd", invariant)}-to-{last.ToString("yyyy-MM-dd", invariant)}";
    }

    private void SavePdf()
    {
        var data = Data;
        Save("", "pdf", "PDF document|*.pdf", stream => stream.Write(_pdf(data)), background: true);
    }

    private void SaveCsv(string? grain)
    {
        if (_range is not { } range || !Enum.TryParse<ExportGrain>(grain, out var chosen)) return;
        var suffix = chosen switch
        {
            ExportGrain.Raw => "-raw",
            ExportGrain.Minute => "-1min",
            _ => "-1h",
        };
        Save(suffix, "csv", "CSV file|*.csv", stream =>
        {
            var lines = _history.Csv(range, chosen) ?? throw new IOException("history can't be read right now.");
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            foreach (var line in lines) writer.WriteLine(line);
        }, background: true);
    }

    /// <summary>Asks where to save, then writes beside the target and moves the file into place, so a failed write leaves any earlier file whole.</summary>
    private void Save(string suffix, string extension, string filter, Action<Stream> write, bool background)
    {
        if (_range is not { } range) return;
        if (_saver.Ask(FileName(range, _zone) + suffix + "." + extension, filter) is not { } path) return;
        void Write()
        {
            var partial = path + ".partial";
            string outcome;
            try
            {
                using (var file = File.Create(partial)) write(file);
                File.Move(partial, path, overwrite: true);
                outcome = "Saved " + Path.GetFileName(path);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Remove(partial);
                outcome = "Couldn't save: " + error.Message;
            }
            _threads.Post(() => Saved = outcome);
        }
        if (background) _threads.Background(Write);
        else Write();
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next save to the same place replaces it.
        }
    }
}
