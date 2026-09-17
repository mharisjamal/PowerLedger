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
    /// <param name="readPowerSupply">The owner's tick for reading a power supply over USB, asked afresh before every read.
    /// Leave null where there is nobody to ask, which leaves any power supply alone altogether.</param>
    public static MachineSensors Create(
        Func<bool> displayOn, Func<bool> sessionLocked,
        Func<double?>? userIdleSeconds = null, ValidatorOptions? validatorOptions = null,
        Action<IReadOnlyList<MonitorFacts>>? monitorsDetected = null,
        Func<bool>? readPowerSupply = null)
    {
        var display = new DisplaySource(displayOn, monitorsDetected);
        List<ISensorSource> sources =
        [
            new EnergyMeterSource(),
            .. Graphics(new NvidiaSource(), static () => new AmdSource(), static () => new ArcSource(), static () => new GpuLoadSource()),
            new BatterySource(),
            new UpsSource(),
            new PsuSource(readPowerSupply),
            new CpuLoadSource(),
            new ActivitySource(sessionLocked, userIdleSeconds),
            display,
        ];
        return new MachineSensors(new Sampler(sources), new SampleValidator(validatorOptions), display);
    }

    /// <summary>NVIDIA's own library when it answers, since it gives the watts and the load together; otherwise AMD's
    /// library and Intel's Level Zero, which measure a Radeon's or an Arc card's watts, and then Windows' GPU load
    /// counters, which give the load and never write watts, so a measured card keeps its own figure. Nothing after NVIDIA
    /// is built while NVIDIA answers, so no two sources fill the same field and none wakes a switched-off card.</summary>
    internal static IReadOnlyList<ISensorSource> Graphics(
        ISensorSource nvidia, Func<ISensorSource> amd, Func<ISensorSource> arc, Func<ISensorSource> load)
        => nvidia.Supported ? [nvidia] : [nvidia, amd(), arc(), load()];

    /// <summary>One validated tick. Never throws.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
        => Validator.Validate(Sampler.Read(timestamp, deltaSeconds));

    public void Dispose() => Sampler.Dispose();
}
