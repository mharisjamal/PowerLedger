using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>
/// The external monitors the service knows (Plan J): what WMI detected, each one's figures from the catalogue, the estimate
/// or the user, the user's choices, and the brightness and power state the App last reported. Detection writes from the
/// sensor thread, the App's reports from the pipe's, and the choices from the loop, which also reads the board every tick
/// for the model and for the status. So every member takes the one lock, and nothing outside the board is called while it
/// is held.
/// </summary>
internal sealed class MonitorBoard(MonitorCatalogue catalogue, TimeProvider clock) : IMonitorDraw
{
    /// <summary>The App reads the brightness every five minutes, so a brightness older than this is from an App that stopped
    /// reporting or a monitor that stopped answering, and counts as unknown.</summary>
    public static readonly TimeSpan BrightnessStale = TimeSpan.FromMinutes(15);

    /// <summary>The App reads whether each monitor is on every minute while the displays are on, so a power state older than
    /// this is from before the displays went off, from an App that stopped reporting or from a monitor that stopped
    /// answering, and counts as unknown.</summary>
    public static readonly TimeSpan PowerStale = TimeSpan.FromMinutes(3);

    /// <summary>The App reads how Windows drives each monitor every minute while the displays are on, so a refresh rate or
    /// HDR state older than this is from before the displays went off, from an App that stopped reporting or from a monitor
    /// Windows no longer drives, and counts as unknown.</summary>
    public static readonly TimeSpan DisplayStale = TimeSpan.FromMinutes(3);

    /// <summary>The largest monitor taken, on a laptop, for a portable one running off it when the user hasn't said.
    /// Portable monitors come in 13 to 17.3 inches; a monitor that gives no size is taken to have a plug of its own.</summary>
    public const double LargestPortableInches = 17.3;

    private readonly Lock _gate = new();

    /// <summary>The last reading for each monitor by instance, kept while it is fresh even if the monitor goes missing from
    /// a detection for a moment, as monitors do while they wake.</summary>
    private readonly Dictionary<string, (double Brightness, long At)> _brightness = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last power state for each monitor by instance, kept as its brightness is.</summary>
    private readonly Dictionary<string, (MonitorPowerState State, long At)> _power = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last refresh rate and HDR state for each monitor by instance, kept as its brightness is.</summary>
    private readonly Dictionary<string, ((double RefreshHz, bool Hdr) Display, long At)> _displays = new(StringComparer.OrdinalIgnoreCase);

    private Figured[] _monitors = [];
    private Dictionary<string, MonitorChoice> _choices = new(StringComparer.Ordinal);

    /// <summary>Whether a monitor with a plug of its own that the user hasn't chosen for counts, as the profile says; one
    /// running off the PC always counts. Until the board is given settings, it goes by the default ones, which count every
    /// monitor.</summary>
    private bool _countedByDefault = true;

    /// <summary>Whether the profile is a laptop's, which a monitor small enough is taken to run off when the user hasn't said.
    /// Until the board is given settings, it goes by the default ones, which are a laptop's.</summary>
    private bool _onLaptop = true;

    /// <summary>What WMI found. Figures are worked out only for monitors that are new or changed, and outside the lock.</summary>
    /// <param name="retired">The token of the sensor set that found them, cancelled once the set is abandoned or thrown away.
    /// A set abandoned because a read hung finishes that read on its own, perhaps long after the set that replaced it has
    /// said what it found, so what it finds then is ignored. The token is cancelled before the replacement is built and is
    /// checked under the lock the list is stored under, so a retired set's list never lands after its replacement's.</param>
    public void Detected(IReadOnlyList<MonitorFacts> monitors, CancellationToken retired = default)
    {
        Figured[] known;
        lock (_gate) known = _monitors;
        Figured[] figured = [.. monitors.Select(facts => Array.Find(known, monitor => monitor.Facts == facts) ?? Figure(facts))];
        var now = clock.GetTimestamp();
        lock (_gate)
        {
            if (retired.IsCancellationRequested) return;
            _monitors = figured;
            DropStale(_brightness, BrightnessStale, now);
            DropStale(_power, PowerStale, now);
            DropStale(_displays, DisplayStale, now);
        }
    }

    /// <summary>The user's choices from the settings' profile, replacing the ones before, with what the profile says for a
    /// monitor they haven't chosen for: whether it counts when it has a plug of its own, and the chassis, which decides
    /// whether it is taken to run off the PC.</summary>
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

    /// <summary>The App's report; a brightness older than <see cref="BrightnessStale"/>, a power state older than
    /// <see cref="PowerStale"/>, and a refresh rate and HDR state older than <see cref="DisplayStale"/>, count as unknown. Only
    /// a monitor that is attached is remembered, so a client can't fill the service's memory with instances it made up.</summary>
    /// <param name="power">Whether each monitor is on, or null from an App that doesn't read it, which leaves the power
    /// states as they were.</param>
    /// <param name="displays">How Windows drives each monitor, or null from an App that doesn't read it, which leaves the
    /// refresh rates and HDR states as they were.</param>
    public void Report(
        IReadOnlyList<MonitorBrightness> readings,
        IReadOnlyList<MonitorPowerReading>? power = null,
        IReadOnlyList<MonitorDisplayReading>? displays = null)
    {
        var now = clock.GetTimestamp();
        lock (_gate)
        {
            foreach (var reading in readings)
            {
                if (!double.IsFinite(reading.Brightness)) continue;
                var monitor = Attached(reading.Instance);
                if (monitor is not null) _brightness[monitor.Facts.Instance] = (Math.Clamp(reading.Brightness, 0, 1), now);
            }
            foreach (var reading in power ?? [])
            {
                if (reading.State is not (MonitorPowerState.On or MonitorPowerState.Standby or MonitorPowerState.Off)) continue;
                var monitor = Attached(reading.Instance);
                if (monitor is not null) _power[monitor.Facts.Instance] = (reading.State, now);
            }
            foreach (var display in displays ?? [])
            {
                if (!double.IsFinite(display.RefreshHz) || display.RefreshHz <= 0) continue;
                var monitor = Attached(display.Instance);
                if (monitor is not null) _displays[monitor.Facts.Instance] = ((display.RefreshHz, display.Hdr), now);
            }
        }
    }

    /// <summary>What every counted monitor draws, split by whether it has a plug of its own or runs off the PC, which always
    /// counts. A monitor that says it is off draws its off figure, and one in standby its sleep figure. One that says it is
    /// on, or hasn't said, draws as the displays are: with the display on, a figure the user typed as it is, or PowerLedger's
    /// own at the monitor's brightness; asleep, its sleep figure.</summary>
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

    /// <summary>The list's figures for this model or, for a model it doesn't know, the estimate from its size and resolution.</summary>
    private Figured Figure(MonitorFacts facts)
    {
        var name = facts.Name.Length > 0 ? facts.Name : $"{facts.Maker} {facts.ProductCode}".Trim();
        if (catalogue.Find(facts.Maker, facts.Name, facts.Inches, facts.Width, facts.Height) is { } listed)
        {
            return new Figured(
                facts, name, listed.OnW, listed.SleepW, listed.OffW, MonitorPower.Anchor(listed.MaxNits), listed.Oled, MonitorSource.Model);
        }
        // An estimate comes mostly from LCD monitors, so the monitor is taken for one.
        var (onW, sleepW, offW, maxNits) = MonitorEstimate.For(facts.Inches, facts.Width, facts.Height, catalogue);
        return new Figured(facts, name, onW, sleepW, offW, MonitorPower.Anchor(maxNits), Oled: false, MonitorSource.Estimate);
    }

    /// <summary>The attached monitor with this instance, in any case, or null. Called with the lock held.</summary>
    private Figured? Attached(string instance)
        => Array.Find(_monitors, monitor => monitor.Facts.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase));

    /// <summary>One monitor as it stands at <paramref name="now"/>. Called with the lock held.</summary>
    private MonitorStatus Describe(Figured monitor, bool displayOn, long now)
    {
        var facts = monitor.Facts;
        var choice = _choices.GetValueOrDefault(facts.Key);
        var onW = choice?.Watts ?? monitor.OnW;
        // A laptop's small monitor is taken for a portable one, running off the laptop's USB-C port; any other has a plug of
        // its own.
        var ownPlugByDefault = !(_onLaptop && facts.Inches is > 0 and <= LargestPortableInches);
        var ownPlug = choice?.OwnPlug ?? ownPlugByDefault;
        // What a monitor running off the PC draws is inside what the PC itself draws, so leaving it out would mean nothing,
        // and it always counts. Only a monitor with a plug of its own counts as the user chose, or as the profile says.
        var counted = !ownPlug || (choice?.Counted ?? _countedByDefault);
        double? brightness = _brightness.TryGetValue(facts.Instance, out var reading) && !IsStale(reading.At, now, BrightnessStale)
            ? reading.Brightness
            : null;
        var state = _power.TryGetValue(facts.Instance, out var said) && !IsStale(said.At, now, PowerStale)
            ? said.State
            : MonitorPowerState.Unknown;
        (double RefreshHz, bool Hdr)? display =
            _displays.TryGetValue(facts.Instance, out var driven) && !IsStale(driven.At, now, DisplayStale) ? driven.Display : null;
        // A figure the user typed is what the monitor draws as they use it, so it is taken as it is, and the brightness is
        // reported only for the user to see. PowerLedger's own figure, from the list or the estimate, is the draw at the
        // list's test luminance, so it is scaled from where that sits on the monitor's brightness scale to the monitor's.
        var onNow = choice?.Watts ?? MonitorPower.At(monitor.OnW, brightness, monitor.Anchor);
        // An LCD panel driven faster than the list's 60 Hz draws more while it is on (spec §3). Nothing is known of what an
        // OLED panel adds, and a figure the user typed is already what the monitor draws as they use it.
        var drivenOn = counted && displayOn && state is (MonitorPowerState.On or MonitorPowerState.Unknown);
        var refreshWatts = drivenOn && choice?.Watts is null && !monitor.Oled
            ? MonitorPower.Refresh(facts.Width, facts.Height, display?.RefreshHz)
            : 0;
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
            OffWatts = monitor.OffW,
            PowerState = state,
            RefreshHz = display?.RefreshHz,
            RefreshWatts = refreshWatts,
            Hdr = display?.Hdr,
            Source = choice?.Watts is null ? monitor.Source : MonitorSource.Typed,
            Counted = counted,
            CountedByDefault = _countedByDefault,
            OwnPlug = ownPlug,
            OwnPlugByDefault = ownPlugByDefault,
            Brightness = brightness,
            // A monitor that says it is off draws its off figure, and one in standby its sleep figure, whatever the displays
            // are doing and whatever the user typed, which is what it draws when on. One that says it is on, or hasn't said,
            // draws as the displays are.
            WattsNow = !counted ? 0 : state switch
            {
                MonitorPowerState.Off => monitor.OffW,
                MonitorPowerState.Standby => monitor.SleepW,
                _ => displayOn ? onNow + refreshWatts : monitor.SleepW,
            },
        };
    }

    /// <summary>Drops the readings older than <paramref name="stale"/>. Called with the lock held.</summary>
    private void DropStale<T>(Dictionary<string, (T Reading, long At)> readings, TimeSpan stale, long now)
    {
        foreach (var instance in readings.Where(reading => IsStale(reading.Value.At, now, stale)).Select(reading => reading.Key).ToList())
        {
            readings.Remove(instance);
        }
    }

    private bool IsStale(long at, long now, TimeSpan stale) => clock.GetElapsedTime(at, now) > stale;

    /// <param name="Name">The name the monitor gives, or its maker and product code when it gives none.</param>
    /// <param name="Anchor">Where <paramref name="OnW"/> sits on the monitor's brightness scale (see
    /// <see cref="MonitorPower.Anchor"/>).</param>
    /// <param name="Oled">Whether the list gives the monitor an OLED panel, which adds no refresh term.</param>
    /// <param name="Source">Where <paramref name="OnW"/> came from: the list or the estimate, never the user.</param>
    private sealed record Figured(
        MonitorFacts Facts, string Name, double OnW, double SleepW, double OffW, double Anchor, bool Oled, MonitorSource Source);
}
