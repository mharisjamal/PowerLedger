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
            .. NvidiaAndTheRest(),
            new BatterySource(),
            new UpsSource(),
            new PsuSource(readPowerSupply),
            new CpuLoadSource(),
            new ActivitySource(sessionLocked, userIdleSeconds),
            display,
        ];
        return new MachineSensors(new Sampler(sources), new SampleValidator(validatorOptions), display);
    }

    /// <summary>The graphics sources for this machine, NVIDIA's built first, since what it reads decides what else is wanted.</summary>
    private static IReadOnlyList<ISensorSource> NvidiaAndTheRest()
    {
        var nvidia = new NvidiaSource();
        return Graphics(
            nvidia, static () => new AmdSource(), static () => new ArcSource(), static () => new GpuLoadSource(),
            otherCards: static () => DiscreteGpu.ReadVideoControllers() is not { } controllers || DiscreteGpu.HasCardBesideNvidia(controllers),
            nvidiaLoadsEveryCard: () => nvidia.LoadsEveryCard);
    }

    /// <summary>
    /// Every card is counted, each read by the best source there is for it. NVIDIA's own library comes first, since it
    /// gives each card's watts and load together. AMD's library and Intel's Level Zero measure a Radeon's or an Arc card's
    /// watts, and Windows' GPU load counters then give every card's load where its library did not, and add each card no
    /// library reads, NVIDIA's included when NVML is missing, with a load for the model to turn into watts. They never
    /// write watts over a library's, so a measured card keeps its own figure and none is counted twice.
    /// While NVIDIA's library answers, the others are built only when there is something left for them: WMI lists a card
    /// that is not NVIDIA's (<paramref name="otherCards"/>, asked without waking anything), or, for the load counters alone,
    /// a card NVML gives no utilisation for (<paramref name="nvidiaLoadsEveryCard"/>). Listing the adapters for the counters
    /// wakes a switched-off card, so a switchable-graphics laptop whose GeForce NVML reads fully never has it done.
    /// A library that will not open partway through closes the ones already built: until the set is handed back there
    /// is nobody else holding them, so anything dropped here keeps its card's interfaces for the life of the process.
    /// </summary>
    /// <param name="otherCards">Whether the machine has a card NVML cannot read; no when left out.</param>
    /// <param name="nvidiaLoadsEveryCard">Whether NVML gives every card's utilisation; yes when left out.</param>
    internal static IReadOnlyList<ISensorSource> Graphics(
        ISensorSource nvidia, Func<ISensorSource> amd, Func<ISensorSource> arc, Func<ISensorSource> load,
        Func<bool>? otherCards = null, Func<bool>? nvidiaLoadsEveryCard = null)
    {
        List<ISensorSource> built = [nvidia];
        try
        {
            if (!nvidia.Supported || otherCards?.Invoke() == true)
            {
                built.Add(amd());
                built.Add(arc());
                built.Add(load());
            }
            else if (nvidiaLoadsEveryCard?.Invoke() == false)
            {
                built.Add(load());
            }
            return built;
        }
        catch (Exception)
        {
            foreach (var source in built)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception)
                {
                    // One that will not close must not hide the others, nor the trouble that brought us here.
                }
            }

            throw;
        }
    }

    /// <summary>One validated tick. Never throws.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
        => Validator.Validate(Sampler.Read(timestamp, deltaSeconds));

    public void Dispose() => Sampler.Dispose();
}
