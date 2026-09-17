using System.Diagnostics;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// What a UPS attached over USB reports its outlets are drawing (spec §4). The UPS is read through the HID Power
/// Device class, the same collection Windows' own UPS battery driver uses, which Windows opens shared; only feature
/// reports are read and nothing is ever set, so PowerLedger cannot change how a UPS behaves.
///
/// The UPS is asked at most every <see cref="ReadEvery"/>, because each question is a control transfer on the USB
/// bus, and the ticks in between carry the last answer. An answer older than <see cref="StaleAfter"/> is no answer.
/// While no UPS is open the HID collections are looked through again every <see cref="LookAgainEvery"/>, so a UPS
/// plugged in later, or one Windows was still setting up when the service started at boot, is found.
///
/// The source owns the UPS fields of the draft and never fails a tick: everything Windows or a device does wrong
/// leaves the fields empty and is said in <see cref="Unavailable"/> instead.
/// </summary>
public sealed class UpsSource : ISensorSource
{
    /// <summary>How often the UPS is asked; the ticks in between carry the last answer.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);

    /// <summary>How old an answer may be before it says nothing about the tick being filled.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>How often the HID collections are looked through while no UPS is open.</summary>
    public static readonly TimeSpan LookAgainEvery = TimeSpan.FromMinutes(1);

    /// <summary>The longest a UPS that keeps going wrong is left alone before it is tried again.</summary>
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(1);

    private const string NothingFound = "no UPS found on USB";

    private readonly IHid _hid;
    private readonly Func<TimeSpan> _clock;
    private Ups? _ups;
    private int _found;
    private bool _attempted;
    private TimeSpan _nextAttemptAt;
    private bool _read;
    private TimeSpan _readAt;
    private double? _watts;
    private UpsPowerSource _source;
    private int _failures;
    private TimeSpan _now;

    /// <summary>Reads the machine's real HID devices.</summary>
    public UpsSource() : this(new WindowsHid(), Elapsed())
    {
    }

    /// <summary>Test seam: any set of HID devices and any clock.</summary>
    internal UpsSource(IHid hid, Func<TimeSpan> clock)
    {
        _hid = hid;
        _clock = clock;
    }

    public string Name => "ups";

    /// <summary>Always true: any Windows machine can be asked whether a UPS is attached, and one may be attached at
    /// any time, so the source is never skipped for good.</summary>
    public bool Supported => true;

    /// <summary>What the status screen should say about the UPS, or null when one is attached and answering. Unlike
    /// the other sources this is also set while the source is supported, because that is the only place to say that
    /// no UPS is attached, that more than one is, or that the one attached reports nothing usable.</summary>
    public string? Unavailable { get; private set; }

    public void Contribute(SampleDraft draft)
    {
        try
        {
            _now = _clock();
            if (!_attempted || _now >= _nextAttemptAt) Attempt(_now);
            Fill(draft, _now);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A UPS must never cost the tick its other readings, so whatever went wrong is only recorded. Closing the
            // UPS means the next attempt starts over, with a fresh handle on whatever is still attached — and each
            // attempt goes through every HID device on the machine, so one that keeps going wrong is left longer and
            // longer alone rather than costing that every five seconds for as long as it stays unhappy.
            Stumble($"reading a UPS over USB failed: {error.Message}");
        }
    }

    public void Dispose() => Close();

    private void Attempt(TimeSpan now)
    {
        _attempted = true;
        if (_ups is null)
        {
            // Set before looking, so a look that goes wrong is not repeated on every tick.
            _nextAttemptAt = now + LookAgainEvery;
            Look();
            if (_ups is null)
            {
                Unavailable = NothingFound;
                return;
            }
        }

        _nextAttemptAt = now + ReadEvery;
        var read = _ups.Read();
        if (!read.Answered)
        {
            // A UPS can miss an answer while its own driver is talking to it, so its last reading is kept until it
            // goes stale. Silent for that long means unplugged, switched off or reset: it is dropped and looked for
            // again, which also gives it a fresh handle.
            if (_read && now - _readAt <= StaleAfter) return;
            Unavailable = $"{_ups.Name} stopped answering";
            _nextAttemptAt = now + LookAgainEvery;
            Close();
            return;
        }

        _read = true;
        _readAt = now;
        _watts = read.Watts;
        _source = read.Watts is null ? UpsPowerSource.None : read.Source;
        _failures = 0;
        Unavailable = Note();
    }

    /// <summary>A round that went wrong: the UPS is let go of and looked for again, and each failure in a row waits
    /// twice as long as the one before, so a UPS that keeps going wrong cannot cost an enumeration of every HID device
    /// on the machine every five seconds. This is <see cref="PsuSource"/>'s rule, for the same reason.</summary>
    private void Stumble(string note)
    {
        Unavailable = note;
        _failures++;
        var backoff = ReadEvery * Math.Pow(2, Math.Min(_failures - 1, 10));
        _nextAttemptAt = _now + (backoff < LongestBackoff ? backoff : LongestBackoff);
        Close();
    }

    private void Fill(SampleDraft draft, TimeSpan now)
    {
        if (_ups is null) return;

        // A UPS that is attached is named even when it has no watts to give, so the status screen can list it.
        draft.UpsName = _ups.Name;
        if (!_read || now - _readAt > StaleAfter) return;

        draft.UpsOutputW = _watts;
        draft.UpsSource = _source;
    }

    /// <summary>
    /// Opens every HID collection in turn and keeps the ones that are a UPS (page 0x84, usage 0x04) or a power summary
    /// (usage 0x24). The collections of one device are one UPS, because a device that publishes both gets an interface
    /// for each. Where there is more than one UPS the first is read and the rest are closed.
    /// </summary>
    private void Look()
    {
        List<IHidCollection> opened = [];
        try
        {
            foreach (var path in _hid.Interfaces())
            {
                IHidCollection? collection = null;
                try
                {
                    collection = _hid.Open(path);
                    if (collection is not null && IsUps(collection))
                    {
                        opened.Add(collection);
                        collection = null;
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // One device Windows will not describe must not hide the others.
                }
                finally
                {
                    // Whatever is not a UPS is closed the moment it has been looked at.
                    Close(collection);
                }
            }

            var devices = opened.GroupBy(collection => collection.Device, StringComparer.OrdinalIgnoreCase).ToList();
            _found = devices.Count;
            if (devices.Count == 0) return;

            var first = devices[0].ToList();
            foreach (var spare in opened.Where(collection => !first.Contains(collection))) Close(spare);
            opened = first;
            _ups = new Ups(first);
            opened = [];                                // the UPS owns them now
        }
        finally
        {
            foreach (var collection in opened) Close(collection);
        }
    }

    private static bool IsUps(IHidCollection collection)
        => collection.UsagePage == Ups.PowerPage && collection.Usage is Ups.UpsUsage or Ups.SummaryUsage;

    private string? Note()
        => _ups is null ? NothingFound
            : !_ups.Offers ? $"{_ups.Name} reports neither its output power nor its load and rating"
            : _found > 1 ? $"{_found} UPSes are attached; only the first, {_ups.Name}, is read"
            : null;

    private void Close()
    {
        var ups = _ups;
        _ups = null;
        _read = false;
        _watts = null;
        _source = UpsPowerSource.None;
        Close(ups);
    }

    private static void Close(IDisposable? closeable)
    {
        try
        {
            closeable?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A handle that will not close is Windows' business, not the tick's.
        }
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
}
