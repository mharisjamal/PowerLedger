using System.Diagnostics;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <param name="Watts">What the card draws now, or null when the library declined this once or the card measures nothing.</param>
/// <param name="Scope">What the watts cover: the whole board where the card measures that, else the chip alone.</param>
/// <param name="Lost">True when the library has stopped answering altogether, as it does when the user logs off while the
/// service keeps running, so every interface must be let go and the library opened again.</param>
internal readonly record struct AmdReading(double? Watts, GpuPowerScope Scope, bool Lost);

/// <summary>How far opening one of AMD's libraries got.</summary>
internal enum AmdLibrary
{
    /// <summary>It opened on a discrete Radeon, which can be read from now on.</summary>
    Reading,

    /// <summary>It answered, and this machine has no discrete Radeon: the answer will not change.</summary>
    NoDiscreteGpu,

    /// <summary>It is not installed, so the next library may still answer.</summary>
    NotInstalled,

    /// <summary>It is installed but would not start or would not list the machine's cards. That can pass: in a service,
    /// ADLX answers only while someone is logged on, so this is tried again rather than settled.</summary>
    WouldNotStart,
}

/// <param name="State">How far the library got.</param>
/// <param name="Gpu">The card it opened, when it opened one.</param>
/// <param name="Reason">Why there is nothing to read, in the words the status screen shows.</param>
internal readonly record struct AmdOpening(AmdLibrary State, IAmdGpu? Gpu, string? Reason);

/// <summary>One of AMD's libraries, opened on one discrete Radeon.</summary>
internal interface IAmdGpu : IDisposable
{
    /// <summary>The card's Plug and Play instance id as the library words it, for asking Windows whether the card is
    /// switched off; null when the library does not say.</summary>
    string? DeviceId { get; }

    /// <summary>What the card draws now. Never throws.</summary>
    AmdReading Read();
}

/// <summary>
/// A discrete Radeon's measured power, through AMD's own libraries: ADLX where the driver installs it, and the older
/// ADL where it does not or where ADLX will not have this driver. Graphics inside the processor are skipped, because
/// the processor's package reading already covers them. Only the watts and their scope are filled here; the load stays
/// with <see cref="GpuLoadSource"/>, which the machine's sensor set runs after this source.
/// </summary>
public sealed class AmdSource : ISensorSource
{
    /// <summary>How long the source waits before opening a library that would not start again. A service that starts
    /// before anyone has logged on is the case this is for: ADLX answers no one in session 0 until a user logs on, and
    /// the card is then picked up within the minute.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(1);

    private readonly Func<AmdOpening> _open;
    private readonly Func<string, bool> _poweredOff;
    private readonly Func<TimeSpan> _clock;
    private IAmdGpu? _gpu;
    private TimeSpan _openedAt;
    private string? _unavailable;

    /// <summary>Opens ADLX, or the older ADL where ADLX is not installed, and keeps the card it finds.</summary>
    public AmdSource() : this(OpenEither, DevicePowerState.IsPoweredOff, Elapsed())
    {
    }

    /// <summary>Test seam: any way of opening a library, of asking whether the card is switched off, and any clock.</summary>
    internal AmdSource(Func<AmdOpening> open, Func<string, bool> poweredOff, Func<TimeSpan> clock)
    {
        _open = open;
        _poweredOff = poweredOff;
        _clock = clock;
        Open();
    }

    public string Name => "amd-gpu";

    /// <summary>False once a library has answered that this machine has no discrete Radeon, or that AMD's driver is not
    /// installed at all. A library that would not start leaves this true, because it may answer later.</summary>
    public bool Supported => _unavailable is null;

    public string? Unavailable => _unavailable;

    public void Contribute(SampleDraft draft)
    {
        if (_gpu is null)
        {
            if (!Supported || _clock() - _openedAt < RetryEvery) return;
            Open();
            if (_gpu is null) return;
        }

        // Windows can say the card is switched off without waking it, which asking the library would.
        var pciDevice = DiscreteGpu.DeviceIdIn(_gpu.DeviceId);
        if (_gpu.DeviceId is { } device && Safely(() => _poweredOff(device), false))
        {
            var off = draft.AddGpu(DiscreteGpu.AmdVendor, pciDevice);
            off.Watts = 0;
            off.Scope = GpuPowerScope.Board;
            return;
        }

        var reading = Safely(_gpu.Read, new AmdReading(null, GpuPowerScope.Board, Lost: true));
        if (reading.Lost)
        {
            Close();
            return;
        }

        // The card's name, rating and load come from Windows' load counters, which recognise it by its PCI ids.
        var card = draft.AddGpu(DiscreteGpu.AmdVendor, pciDevice);
        card.Watts = reading.Watts;
        if (reading.Watts is not null) card.Scope = reading.Scope;
    }

    public void Dispose() => Close();

    /// <summary>ADLX first, because it measures the whole board on the cards that can; the older ADL only where ADLX is
    /// not installed, or where it is installed and would not start, since then it may be an old driver ADLX declines.</summary>
    internal static AmdOpening Choose(AmdOpening adlx, Func<AmdOpening> adl)
    {
        if (adlx.State is AmdLibrary.Reading or AmdLibrary.NoDiscreteGpu) return adlx;

        var older = adl();
        if (older.State is AmdLibrary.Reading) return older;

        // ADLX installed but silent settles nothing: the card it would find may still be there once it starts.
        return adlx.State is AmdLibrary.WouldNotStart ? adlx : older;
    }

    private static AmdOpening OpenEither() => Choose(Adlx.Open(), Adl.Open);

    private void Open()
    {
        _openedAt = _clock();
        var opening = Safely(_open, new AmdOpening(AmdLibrary.WouldNotStart, null, "AMD's libraries would not start"));
        _gpu = opening.Gpu;
        _unavailable = opening.State is AmdLibrary.NoDiscreteGpu or AmdLibrary.NotInstalled ? opening.Reason : null;
    }

    private void Close()
    {
        var gpu = _gpu;
        _gpu = null;
        _openedAt = _clock();
        if (gpu is null) return;
        Safely<object?>(() =>
        {
            gpu.Dispose();
            return null;
        }, null);
    }

    /// <summary>A sensor source contributes what it can and never throws, so a library that misbehaves costs its own
    /// fields and nothing else. Out of memory is left to the runtime.</summary>
    private static T Safely<T>(Func<T> call, T whenItFails)
    {
        try
        {
            return call();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return whenItFails;
        }
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
}
