using System.Diagnostics;

namespace PowerLedger.Sensors;

/// <summary>
/// The DC output a power supply reports over USB (spec §5). Three makers' supplies answer over HID: Corsair's HXi and
/// RMi, NZXT's E series and Thermaltake's DPS G. One is read at most every two seconds, with read commands only, and
/// only while the owner leaves the tick on and the maker's own program is not running: such a supply answers one
/// program at a time, and PowerLedger gives way to iCUE, CAM or Thermaltake's app rather than spoiling both readings.
/// A supply that stops answering is tried less and less often, and nothing here is thrown at the sampler: the worst a
/// supply can cost is its own two fields.
/// </summary>
public sealed class PsuSource : ISensorSource
{
    /// <summary>The device is asked this often at most, whatever the sample interval is.</summary>
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(2);

    /// <summary>A reading older than this is no longer offered: a stale figure is worse than none.</summary>
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(10);

    /// <summary>Windows is still enumerating USB devices while the service runs, at boot and after a resume, so a
    /// machine that shows no supply at once is looked at again for a minute before it is taken to have none.</summary>
    private static readonly TimeSpan LookFor = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(10);

    /// <summary>The longest a supply that will not answer is left alone before it is tried again.</summary>
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(60);

    /// <summary>The shortest reports any of the three protocols can work with, report ID and all.</summary>
    private const int SmallestReport = 10;

    private readonly IHidPort _hid;
    private readonly Func<bool> _readPowerSupply;
    private readonly Func<IReadOnlyCollection<string>> _programs;
    private readonly Func<TimeSpan> _clock;
    private readonly Action<TimeSpan> _pause;
    private readonly TimeSpan _lookUntil;

    private HidDevice? _device;
    private PsuModel? _model;
    private PsuSession? _session;
    private string? _name;
    private string? _note;
    private string? _heldBy;
    private double _watts;
    private TimeSpan? _readAt;
    private TimeSpan _nextReadAt;
    private TimeSpan _nextLookAt;
    private int _failures;

    /// <param name="readPowerSupply">The owner's tick in Settings, read afresh every time: while it answers false
    /// nothing at all is sent to the device. Leave it null where there is nobody to ask, which reads no supply.</param>
    public PsuSource(Func<bool>? readPowerSupply = null)
        : this(new WindowsHidPort(), readPowerSupply ?? (static () => false), RunningPrograms, Uptime, Thread.Sleep)
    {
    }

    /// <summary>Test seam: any HID layer, list of running programs and clock.</summary>
    internal PsuSource(
        IHidPort hid, Func<bool> readPowerSupply, Func<IReadOnlyCollection<string>> programs,
        Func<TimeSpan> clock, Action<TimeSpan>? pause = null)
    {
        _hid = hid;
        _readPowerSupply = readPowerSupply;
        _programs = programs;
        _clock = clock;
        _pause = pause ?? (static _ => { });
        _lookUntil = clock() + LookFor;
        Look(clock());
    }

    public string Name => "power-supply";

    /// <summary>True while a supply is there to read, and while there is still time for one to turn up.</summary>
    public bool Supported => _device is not null || _clock() < _lookUntil;

    /// <summary>Why there is no reading: no supply, one the owner has turned off, one left to its maker's program, or
    /// one that did not answer. Null while a supply is being read.</summary>
    public string? Unavailable => _device is null
        ? (_clock() < _lookUntil ? "looking for a power supply on USB" : PsuModels.NoneFound)
        : _note;

    public void Contribute(SampleDraft draft)
    {
        try
        {
            Fill(draft);
        }
        catch (Exception error)
        {
            // A sensor is never allowed to spoil a tick: the supply's fields stay as they are and the status says why.
            Stumble(_clock(), error.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            Close();
        }
        catch (Exception)
        {
            // A device that will not close is Windows' business; it must not take the tick or the process down.
        }
    }

    /// <summary>The processes running now, by name. The service is in session 0 and still sees every session's.</summary>
    private static IReadOnlyCollection<string> RunningPrograms()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Select(process => process.ProcessName).ToList();
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>A clock that only goes forward, so neither the throttle nor the staleness rule is fooled by the clock
    /// being set or by a daylight saving change.</summary>
    private static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void Fill(SampleDraft draft)
    {
        var now = _clock();
        if (_device is null)
        {
            if (now < _nextLookAt || now >= _lookUntil) return;
            Look(now);
            if (_device is null) return;
        }

        try
        {
            draft.PsuOutputW = Reading(now);
        }
        finally
        {
            // The supply is named whether or not it was read, so Settings can list it and offer its tick.
            draft.PsuName = _name;
        }
    }

    /// <summary>The watts to report this tick: a fresh enough answer from the supply, or nothing at all.</summary>
    private double? Reading(TimeSpan now)
    {
        if (!_readPowerSupply())
        {
            Close();
            _note = $"reading the {_name} is turned off";
            return null;
        }

        if (now >= _nextReadAt)
        {
            _nextReadAt = now + ReadEvery;
            _heldBy = PsuPrograms.Holding(_model!.Family, _programs());
            if (_heldBy is not null) Close();
            else ReadOnce(now);
        }

        if (_heldBy is { } program)
        {
            _note = $"the {_name} is left to {program}, which is running";
            return null;
        }

        return _readAt is { } at && now - at <= Keep ? _watts : null;
    }

    /// <summary>Looks for a supply Windows lists now. The first one found is the one read: a PC has one supply, and
    /// reading a second would be a total of something other than this machine.</summary>
    private void Look(TimeSpan now)
    {
        _nextLookAt = now + LookEvery;
        foreach (var listed in Listed())
        {
            if (PsuModels.Find(listed.VendorId, listed.ProductId) is not { } model) continue;
            if (listed.OutputReportLength < SmallestReport || listed.InputReportLength < SmallestReport) continue;
            _device = listed;
            _model = model;
            _name = model.Name;
            return;
        }
    }

    private IReadOnlyList<HidDevice> Listed()
    {
        try
        {
            return _hid.Find(PsuModels.Known);
        }
        catch (Exception)
        {
            // Windows not listing its devices is not this machine saying it has no supply: it is looked for again.
            return [];
        }
    }

    private void ReadOnce(TimeSpan now)
    {
        _session ??= Start();
        if (_session is null)
        {
            Stumble(now, $"the {_name} would not open");
            return;
        }

        var watts = _session.ReadWatts(now + PsuSession.Budget);
        if (_session.Name is { } named) _name = named;
        if (watts is not { } value)
        {
            Stumble(now, $"the {_name} did not answer");
            return;
        }

        _watts = value;
        _readAt = now;
        _failures = 0;
        _note = null;
    }

    private PsuSession? Start()
    {
        if (_hid.Open(_device!) is not { } link) return null;
        var wire = new PsuWire(link, _device!.OutputReportLength, Rules(_model!.Family), _clock, _pause);
        return _model.Family switch
        {
            PsuFamily.Corsair => new CorsairSession(wire),
            PsuFamily.Nzxt => new NzxtSession(wire),
            _ => new DpsgSession(wire),
        };
    }

    private static Func<byte[], bool> Rules(PsuFamily family) => family switch
    {
        PsuFamily.Corsair => CorsairSession.Allows,
        PsuFamily.Nzxt => NzxtSession.Allows,
        _ => DpsgSession.Allows,
    };

    /// <summary>A read that came to nothing: the device is given up and tried again later, less and less often, so a
    /// supply that has stopped answering cannot cost a timeout on every other tick.</summary>
    private void Stumble(TimeSpan now, string note)
    {
        Close();
        _failures++;
        _note = note;
        var backoff = ReadEvery * Math.Pow(2, Math.Min(_failures - 1, 10));
        _nextReadAt = now + (backoff < LongestBackoff ? backoff : LongestBackoff);
    }

    private void Close()
    {
        _session?.Dispose();
        _session = null;
    }
}
