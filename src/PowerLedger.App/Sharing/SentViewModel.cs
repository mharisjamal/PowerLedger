using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>One upload kept on this PC: the day it describes and its size as sent, with the file to open for its JSON.</summary>
internal sealed record SentRow(string Day, string Size, string Path);

/// <summary>
/// "What's been sent" (data-sharing design §2): every upload under the Sent folder, newest first, with "Send now" and,
/// for each row, its JSON opened through <see cref="PayloadWindow"/>. A missing or empty folder is the empty state.
/// </summary>
internal sealed class SentViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly string _folder;
    private readonly CultureInfo _culture;
    private readonly Action<string> _openPayload;
    private IReadOnlyList<SentRow> _rows = [];
    private string? _empty;
    private string? _message;

    public SentViewModel(IServiceLink link, UiThreads threads, string folder, CultureInfo culture, Action<string> openPayload)
    {
        _link = link;
        _threads = threads;
        _folder = folder;
        _culture = culture;
        _openPayload = openPayload;
        SendNow = new RelayCommand(() => _ = SendNowAsync());
        Refresh();
    }

    public IReadOnlyList<SentRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    /// <summary>Whether <see cref="Rows"/> has anything to show.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>"Nothing has been sent from this PC.", or null once a row exists.</summary>
    public string? Empty { get => _empty; private set => SetProperty(ref _empty, value); }

    /// <summary>What "Send now" came back as, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public ICommand SendNow { get; }

    /// <summary>Opens a row's JSON. Call on the UI thread.</summary>
    public void Open(SentRow row) => _openPayload(row.Path);

    /// <summary>Reads the folder again. Call on the UI thread.</summary>
    public void Refresh()
    {
        Rows = List(_folder, _culture);
        OnPropertyChanged(nameof(HasRows));
        Empty = Rows.Count == 0 ? "Nothing has been sent from this PC." : null;
    }

    /// <summary>Asks the service to send every complete day waiting now, and reads the folder again. Call on the UI thread.</summary>
    internal async Task SendNowAsync()
    {
        var result = await _link.SendNowAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            Message = result.Message;
            Refresh();
        });
    }

    /// <summary>Every <c>*.json.gz</c> under <paramref name="folder"/>, newest day first; none when the folder is missing
    /// or empty.</summary>
    internal static IReadOnlyList<SentRow> List(string folder, CultureInfo culture)
    {
        if (!Directory.Exists(folder)) return [];
        return [.. new DirectoryInfo(folder).GetFiles("*.json.gz")
            .Select(file => (file, day: Day(file.Name)))
            .OrderByDescending(row => row.day)
            .Select(row => new SentRow(row.day.ToString("d MMM yyyy", culture), Format.Kb(row.file.Length, culture), row.file.FullName))];
    }

    /// <summary>The day a Sent file's name encodes, <c>&lt;yyyy-MM-dd&gt;.json.gz</c>; the earliest possible day when a name
    /// does not parse, so a stray file sorts last rather than breaking the list.</summary>
    private static DateTime Day(string fileName)
    {
        const string suffix = ".json.gz";
        var text = fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^suffix.Length] : fileName;
        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : DateTime.MinValue;
    }
}
