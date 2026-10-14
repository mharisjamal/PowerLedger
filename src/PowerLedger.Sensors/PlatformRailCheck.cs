using System.Globalization;
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// Checks a whole-platform rail (Snapdragon X's SYS, Intel's PSYS) against the one measurement that can say whether it holds
/// the whole machine: the battery's discharge. On battery the rail should read what the battery gives, since nothing else
/// powers the machine then. Whole minutes on battery are summed, the rail's energy and the battery's, and once five of the
/// last ten agree within 0.9 to 1.1 of each other the rail is trusted as the total; outside that it is set aside, the total
/// is worked out from the parts as on a machine without the rail, and one line goes to the log. Until the battery has had
/// its say the rail is trusted, since on a machine that never runs on battery it is the best figure there is. The first
/// minute after the mains goes is left out, while the battery's own rate catches up.
/// One check lasts as long as the service, since a sensor set is built afresh after every resume. Thread-safe: an abandoned
/// sensor set may still be reading while its replacement does, and the status screen asks from another thread.
/// </summary>
public sealed class PlatformRailCheck
{
    public const double LowestRatio = 0.9;
    public const double HighestRatio = 1.1;
    public const int MinutesNeeded = 5;
    public const int MinutesKept = 10;

    private const double MinuteSeconds = 60;
    private const double SettleSeconds = 60;

    /// <summary>A tick longer than this is a gap (a sleep, a hang), not time on battery.</summary>
    private const double LongestTickSeconds = 30;

    private readonly object _gate = new();
    private readonly Queue<(double Rail, double Discharge)> _minutes = new();
    private double _railJoules;
    private double _dischargeJoules;
    private double _seconds;
    private double _onBatteryFor;
    private bool? _agrees;
    private double _ratio;

    /// <summary>The check for this machine, for as long as the service runs.</summary>
    public static PlatformRailCheck Machine { get; } = new();

    /// <summary>True until the battery's discharge has shown the rail to disagree.</summary>
    public bool Trusted
    {
        get { lock (_gate) return _agrees != false; }
    }

    /// <summary>Why the rail is set aside, for the status screen; null while it is trusted.</summary>
    public string? Status
    {
        get
        {
            lock (_gate)
            {
                return _agrees == false
                    ? Invariant($"the platform rail read {_ratio:0.00} times the battery's discharge, so totals are worked out from the parts")
                    : null;
            }
        }
    }

    /// <summary>Learns from one validated tick and gives it back, without its platform figure while the rail is set aside.</summary>
    /// <param name="note">Told one line whenever the verdict changes.</param>
    public Sample Apply(Sample sample, Action<string>? note)
    {
        string? said;
        bool trusted;
        lock (_gate)
        {
            said = Observe(sample);
            trusted = _agrees != false;
        }
        if (said is not null) note?.Invoke(said);
        return trusted || sample.PlatformW is null ? sample : sample with { PlatformW = null };
    }

    /// <summary>Adds the tick to this minute on battery, and closes the minute once it is full. Gives the line to log when the
    /// verdict changed.</summary>
    private string? Observe(Sample s)
    {
        if (!s.OnBattery)
        {
            _onBatteryFor = 0;
            StartMinute();
            return null;
        }

        var seconds = s.DeltaSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return null;
        if (seconds > LongestTickSeconds)
        {
            // A gap: what was gathered either side of it is not one stretch on battery.
            _onBatteryFor = 0;
            StartMinute();
            return null;
        }

        _onBatteryFor += seconds;
        if (_onBatteryFor <= SettleSeconds || s.Suspect || !s.HasDischargeRate || s.BatteryRateW is not { } discharge
            || s.PlatformW is not { } rail || !double.IsFinite(rail) || rail < 0)
        {
            return null;
        }

        _railJoules += rail * seconds;
        _dischargeJoules += discharge * seconds;
        _seconds += seconds;
        if (_seconds < MinuteSeconds) return null;

        _minutes.Enqueue((_railJoules, _dischargeJoules));
        while (_minutes.Count > MinutesKept) _minutes.Dequeue();
        StartMinute();
        if (_minutes.Count < MinutesNeeded) return null;

        var railTotal = _minutes.Sum(minute => minute.Rail);
        var dischargeTotal = _minutes.Sum(minute => minute.Discharge);
        if (!(dischargeTotal > 0)) return null;
        _ratio = railTotal / dischargeTotal;
        var agrees = _ratio is >= LowestRatio and <= HighestRatio;
        if (agrees == _agrees) return null;
        _agrees = agrees;
        return agrees
            ? Invariant($"The platform rail agrees with the battery's discharge ({_ratio:0.00} times it over {_minutes.Count} minutes on battery), so it is trusted as the total")
            : Invariant($"The platform rail read {_ratio:0.00} times the battery's discharge over {_minutes.Count} minutes on battery, outside {LowestRatio} to {HighestRatio}, so totals are worked out from the parts until it agrees again");
    }

    private void StartMinute()
    {
        _railJoules = 0;
        _dischargeJoules = 0;
        _seconds = 0;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
