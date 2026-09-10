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
    public static MachineSensors Create(
        Func<bool> displayOn, Func<bool> sessionLocked,
        Func<double?>? userIdleSeconds = null, ValidatorOptions? validatorOptions = null)
    {
        var display = new DisplaySource(displayOn);
        var sources = new List<ISensorSource>
        {
            new EnergyMeterSource(),
            new NvidiaSource(),
            new BatterySource(),
            new CpuLoadSource(),
            new ActivitySource(sessionLocked, userIdleSeconds),
            display,
        };
        return new MachineSensors(new Sampler(sources), new SampleValidator(validatorOptions), display);
    }

    /// <summary>One validated tick. Never throws.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
        => Validator.Validate(Sampler.Read(timestamp, deltaSeconds));

    public void Dispose() => Sampler.Dispose();
}
