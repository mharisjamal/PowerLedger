using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>
/// The external monitors the service knows (Plan J): what WMI detected, each one's figure from the catalogue, the estimate
/// or the user, the user's choices, and the brightness the App last reported. Detection writes from the sensor thread, the
/// App's reports from the pipe's, and the choices from the loop, which also reads the board every tick for the model and
/// for the status. So every member takes the one lock, and nothing outside the board is called while it is held.
/// </summary>
internal sealed class MonitorBoard(MonitorCatalogue catalogue, TimeProvider clock) : IMonitorDraw
{
    /// <summary>The App reports every five minutes, so a brightness older than this is from an App that stopped reporting
    /// or a monitor that stopped answering, and counts as unknown.</summary>
    public static readonly TimeSpan BrightnessStale = TimeSpan.FromMinutes(15);

    /// <summary>The largest monitor taken, on a laptop, for a portable one running off it when the user hasn't said.
    /// Portable monitors come in 13 to 17.3 inches; a monitor that gives no size is taken to have a plug of its own.</summary>
    public const double LargestPortableInches = 17.3;

    private readonly Lock _gate = new();

    /// <summary>The last reading for each monitor by instance, kept while it is fresh even if the monitor goes missing from
    /// a detection for a moment, as monitors do while they wake.</summary>
    private readonly Dictionary<string, (double Brightness, long At)> _brightness = new(StringComparer.OrdinalIgnoreCase);

    private Figured[] _monitors = [];
    private Dictionary<string, MonitorChoice> _choices = new(StringComparer.Ordinal);

    /// <summary>Whether a monitor the user hasn't chosen for counts, as the profile says. Until the board is given settings,
    /// it goes by the default ones, which count every monitor.</summary>
    private bool _countedByDefault = true;

    /// <summary>Whether the profile is a laptop's, which a monitor small enough is taken to run off when the user hasn't said.
    /// Until the board is given settings, it goes by the default ones, which are a laptop's.</summary>
    private bool _onLaptop = true;

    /// <summary>What WMI found. Figures are worked out only for monitors that are new or changed, and outside the lock.</summary>
    public void Detected(IReadOnlyList<MonitorFacts> monitors)
    {
        Figured[] known;
        lock (_gate) known = _monitors;
        Figured[] figured = [.. monitors.Select(facts => Array.Find(known, monitor => monitor.Facts == facts) ?? Figure(facts))];
        var now = clock.GetTimestamp();
        lock (_gate)
        {
            _monitors = figured;
            foreach (var instance in _brightness.Where(reading => IsStale(reading.Value.At, now)).Select(reading => reading.Key).ToList())
            {
                _brightness.Remove(instance);
            }
        }
    }

    /// <summary>The user's choices from the settings' profile, replacing the ones before, with what the profile says for a
    /// monitor they haven't chosen for: whether it counts, and the chassis, which decides whether it is taken to run off
    /// the PC.</summary>
    public void Choose(MachineProfile profile)
    {
        var byKey = new Dictionary<string, MonitorChoice>(StringComparer.Ordinal);
        foreach (var choice in profile.Monitors) byKey[choice.Key] = choice;
        lock (_gate)
        {
            _choices = byKey;
            _countedByDefault = profile.CountMonitorsByDefault;
            _onLaptop = profile.Chassis == ChassisKind.Laptop;
        }
    }

    /// <summary>The App's report; a brightness older than <see cref="BrightnessStale"/> counts as unknown. Only a monitor
    /// that is attached is remembered, so a client can't fill the service's memory with instances it made up.</summary>
    public void Report(IReadOnlyList<MonitorBrightness> readings)
    {
        var now = clock.GetTimestamp();
        lock (_gate)
        {
            foreach (var reading in readings)
            {
                if (!double.IsFinite(reading.Brightness)) continue;
                var monitor = Array.Find(_monitors, m => m.Facts.Instance.Equals(reading.Instance, StringComparison.OrdinalIgnoreCase));
                if (monitor is not null) _brightness[monitor.Facts.Instance] = (Math.Clamp(reading.Brightness, 0, 1), now);
            }
        }
    }

    /// <summary>What every counted monitor draws, split by whether it has a plug of its own or runs off the PC: with the
    /// display on, a figure the user typed as it is, or PowerLedger's own at the monitor's brightness; asleep, its sleep
    /// figure.</summary>
    public MonitorWatts Watts(bool displayOn)
    {
        var now = clock.GetTimestamp();
        lock (_gate)
        {
            var ownPlug = 0.0;
            var fromPc = 0.0;
            foreach (var monitor in _monitors)
            {
                var status = Describe(monitor, displayOn, now);
                if (status.OwnPlug) ownPlug += status.WattsNow;
                else fromPc += status.WattsNow;
            }
            return new MonitorWatts(ownPlug, fromPc);
        }
    }

    /// <summary>Each monitor as the App shows it, in the order detection gave them; empty when there are none.</summary>
    public IReadOnlyList<MonitorStatus> Status(bool displayOn)
    {
        var now = clock.GetTimestamp();
        lock (_gate) return [.. _monitors.Select(monitor => Describe(monitor, displayOn, now))];
    }

    /// <summary>The list's figure for this model or, for a model it doesn't know, the estimate from its size and resolution.</summary>
    private Figured Figure(MonitorFacts facts)
    {
        var name = facts.Name.Length > 0 ? facts.Name : $"{facts.Maker} {facts.ProductCode}".Trim();
        if (catalogue.Find(facts.Maker, facts.Name, facts.Inches, facts.Width, facts.Height) is { } listed)
        {
            return new Figured(facts, name, listed.OnW, listed.SleepW, MonitorSource.Model);
        }
        var (onW, sleepW) = MonitorEstimate.For(facts.Inches, facts.Width, facts.Height, catalogue);
        return new Figured(facts, name, onW, sleepW, MonitorSource.Estimate);
    }

    /// <summary>One monitor as it stands at <paramref name="now"/>. Called with the lock held.</summary>
    private MonitorStatus Describe(Figured monitor, bool displayOn, long now)
    {
        var facts = monitor.Facts;
        var choice = _choices.GetValueOrDefault(facts.Key);
        var onW = choice?.Watts ?? monitor.OnW;
        var counted = choice?.Counted ?? _countedByDefault;
        // A laptop's small monitor is taken for a portable one, running off the laptop's USB-C port; any other has a plug of
        // its own.
        var ownPlugByDefault = !(_onLaptop && facts.Inches is > 0 and <= LargestPortableInches);
        double? brightness = _brightness.TryGetValue(facts.Instance, out var reading) && !IsStale(reading.At, now) ? reading.Brightness : null;
        // A figure the user typed is what the monitor draws as they use it, so it is taken as it is, and the brightness is
        // reported only for the user to see. PowerLedger's own figure, from the list or the estimate, is the draw at the
        // list's test brightness, so it is scaled to the monitor's.
        var onNow = choice?.Watts ?? MonitorPower.At(monitor.OnW, brightness);
        return new MonitorStatus
        {
            Key = facts.Key,
            Instance = facts.Instance,
            Name = monitor.Name,
            Inches = facts.Inches,
            Width = facts.Width,
            Height = facts.Height,
            OnWatts = onW,
            SleepWatts = monitor.SleepW,
            Source = choice?.Watts is null ? monitor.Source : MonitorSource.Typed,
            Counted = counted,
            CountedByDefault = _countedByDefault,
            OwnPlug = choice?.OwnPlug ?? ownPlugByDefault,
            OwnPlugByDefault = ownPlugByDefault,
            Brightness = brightness,
            WattsNow = !counted ? 0 : displayOn ? onNow : monitor.SleepW,
        };
    }

    private bool IsStale(long at, long now) => clock.GetElapsedTime(at, now) > BrightnessStale;

    /// <param name="Name">The name the monitor gives, or its maker and product code when it gives none.</param>
    /// <param name="Source">Where <paramref name="OnW"/> came from: the list or the estimate, never the user.</param>
    private sealed record Figured(MonitorFacts Facts, string Name, double OnW, double SleepW, MonitorSource Source);
}
