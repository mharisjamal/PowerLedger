using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// The sensor set this machine can offer, assembled once and read every tick. The Service and the console
/// preview both build it through here so they cannot drift apart. Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class MachineSensors : IDisposable
{
    private MachineSensors(Sampler sampler, SampleValidator validator, DisplaySource display)
    {
        Sampler = sampler;
        Validator = validator;
        Display = display;
    }

    /// <summary>The composed sources, for per-source health on the status screen.</summary>
    public Sampler Sampler { get; }

    /// <summary>The validator, for its suspect count.</summary>
    public SampleValidator Validator { get; }

    /// <summary>The display source, so the caller can force a refresh after a resume or a monitor change.</summary>
    public DisplaySource Display { get; }

    /// <param name="displayOn">Whether the screen is lit; the Service supplies this from its power notifications.</param>
    /// <param name="sessionLocked">Whether the console session is locked; the Service supplies this from its session notifications.</param>
    /// <param name="userIdleSeconds">Idle time from the user's session. Leave null in a process that runs in that session;
    /// the Service, in session 0, must supply it.</param>
    /// <param name="validatorOptions">Overrides for the plausible ranges and windows.</param>
    /// <param name="monitorsDetected">Told which external monitors are attached, from the thread that reads the set: once WMI
    /// first answers, and whenever they change (checked once a minute). Leave null to skip reading them.</param>
    public static MachineSensors Create(
        Func<bool> displayOn, Func<bool> sessionLocked,
        Func<double?>? userIdleSeconds = null, ValidatorOptions? validatorOptions = null,
        Action<IReadOnlyList<MonitorFacts>>? monitorsDetected = null)
    {
        var display = new DisplaySource(displayOn, monitorsDetected);
        List<ISensorSource> sources =
        [
            new EnergyMeterSource(),
            .. Graphics(new NvidiaSource(), static () => new ArcSource(), static () => new GpuLoadSource()),
            new BatterySource(),
            new CpuLoadSource(),
            new ActivitySource(sessionLocked, userIdleSeconds),
            display,
        ];
        return new MachineSensors(new Sampler(sources), new SampleValidator(validatorOptions), display);
    }

    /// <summary>NVIDIA's own library when it answers, and nothing else: the others are only built when it has nothing to
    /// say, so they never fill the discrete GPU's fields beside it. Otherwise Intel's Level Zero, which measures an Arc
    /// card's watts, and then Windows' GPU load counters, which give an AMD or Intel card's load and never write watts,
    /// so a measured card keeps its own figure.</summary>
    internal static IReadOnlyList<ISensorSource> Graphics(ISensorSource nvidia, Func<ISensorSource> arc, Func<ISensorSource> amdOrIntel)
        => nvidia.Supported ? [nvidia] : [nvidia, arc(), amdOrIntel()];

    /// <summary>One validated tick. Never throws.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
        => Validator.Validate(Sampler.Read(timestamp, deltaSeconds));

    public void Dispose() => Sampler.Dispose();
}
