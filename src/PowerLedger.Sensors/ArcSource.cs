using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// An Intel Arc card's watts, measured by the card itself through Level Zero Sysman (spec §2). The card keeps a
/// monotonic energy counter and says when it read it, so watts are the energy used between two readings over the time
/// between them: the first reading of all has none. The card-wide power domain is read where the card has one, and its
/// package domain otherwise, which leaves the memory, regulators and fans for the model to estimate.
/// Graphics built into the processor are skipped, because the processor's own package reading already counts them.
/// The card's load is not read here: Windows' GPU load counters give it, and they never write watts.
/// While Windows has the card switched off, as a switchable-graphics laptop does almost all the time, it draws next to
/// nothing and Level Zero is not asked at all, since reading the counter could wake it.
/// Never throws. A library that is missing, a driver that fails and a card that has gone all leave the watts to the
/// model's load estimate instead, since a source that threw every tick would only be backed off and retried.
/// </summary>
public sealed class ArcSource : ISensorSource
{
    private const int Success = 0;
    private const string NoLibrary = "no Level Zero library installed";
    private const string OldLibrary = "the Level Zero library is too old";
    private const string NoStart = "Level Zero would not start";
    private const string NoCard = "no Intel Arc discrete GPU";
    private const string NoDomain = "the Intel Arc GPU reports no card or package power";

    private readonly ISysman _sysman;
    private readonly Func<bool> _poweredOff = static () => false;
    private readonly IntPtr _domain;
    private readonly GpuPowerScope _scope = GpuPowerScope.Board;
    private SysmanEnergy? _previous;

    /// <summary>Opens the graphics driver's own Level Zero library and looks for a card in it.</summary>
    public ArcSource() : this(new LevelZeroSysman(), WindowsPowerState)
    {
    }

    /// <summary>Test seam: any Level Zero, any way of asking whether Windows has the card it found switched off, and the
    /// option of reading integrated graphics, which only a hardware test wants.</summary>
    internal ArcSource(ISysman sysman, Func<SysmanDevice, Func<bool>> powerState, bool includeIntegrated = false)
    {
        _sysman = sysman;
        try
        {
            if (Choose(sysman, includeIntegrated, out var unavailable) is { } card)
            {
                _domain = card.Domain;
                _scope = card.Scope;
                _poweredOff = powerState(card.Device);
            }
            Unavailable = unavailable;
        }
        catch (DllNotFoundException)
        {
            Unavailable = NoLibrary;
        }
        catch (EntryPointNotFoundException)
        {
            Unavailable = OldLibrary;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A constructor that threw would stop the whole sensor set from being built.
            Unavailable = error.Message;
        }
        Supported = Unavailable is null;
    }

    public string Name => "arc-gpu";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (!Supported) return;
        draft.DGpuPresent = true;

        // A machine can have a discrete Radeon and a discrete Arc card at once, and the tick has one field between
        // them for the card's watts. AMD's library is read before this one, so whatever it measured stands: writing
        // over it would lose the Radeon's draw, and an Arc that Windows has switched off would replace it with a nought.
        // Not asking Level Zero at all is also what keeps this from waking a card for a reading nobody will use.
        if (draft.DGpuW is not null) return;

        draft.DGpuScope = _scope;
        try
        {
            draft.DGpuW = Watts();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            draft.DGpuW = null;
        }
    }

    /// <summary>Level Zero has nothing to close: Sysman lives as long as the process.</summary>
    public void Dispose()
    {
    }

    /// <summary>Whether Windows has this card switched off, asked of the display adapter with the PCI ids Level Zero
    /// reported. A card Windows cannot find counts as on, which never hides real draw.</summary>
    internal static Func<bool> WindowsPowerState(SysmanDevice device)
    {
        var found = DevicePowerState.FindDisplayAdapter($@"PCI\VEN_{device.VendorId:X4}&DEV_{device.DeviceId:X4}");
        return found is null ? static () => false : () => DevicePowerState.IsPoweredOff(found);
    }

    /// <summary>The first discrete Intel GPU with a power domain worth reading, or null and why there is none.</summary>
    private static Card? Choose(ISysman sysman, bool includeIntegrated, out string? unavailable)
    {
        if (sysman.Init() != Success)
        {
            unavailable = NoStart;
            return null;
        }

        unavailable = NoCard;
        if (sysman.GetDrivers(out var drivers) != Success) return null;
        foreach (var driver in drivers)
        {
            if (sysman.GetDevices(driver, out var devices) != Success) continue;
            foreach (var device in devices)
            {
                if (sysman.GetDeviceProperties(device, out var properties) != Success) continue;
                if (!properties.IsGpu || (properties.Integrated && !includeIntegrated)) continue;
                if (Measures(sysman, device) is not { } domain)
                {
                    unavailable = NoDomain;
                    continue;
                }
                unavailable = null;
                return new Card(properties, domain.Handle, domain.Scope);
            }
        }
        return null;
    }

    /// <summary>The card's own card-wide domain, or its package domain, or null when it reports neither.</summary>
    private static (IntPtr Handle, GpuPowerScope Scope)? Measures(ISysman sysman, IntPtr device)
    {
        if (sysman.GetPowerDomains(device, out var domains) != Success) return null;

        IntPtr? package = null;
        foreach (var domain in domains)
        {
            if (sysman.GetPowerDomain(domain, out var kind) != Success) continue;
            if (kind == PowerDomain.Card) return (domain, GpuPowerScope.Board);
            if (kind == PowerDomain.Package) package ??= domain;
        }
        return package is { } found ? (found, GpuPowerScope.Package) : null;
    }

    /// <summary>The watts the card drew since the last reading, or null when this tick cannot say.</summary>
    private double? Watts()
    {
        if (_poweredOff())
        {
            // A card Windows has switched off draws next to nothing, and asking Level Zero for its counter could wake
            // it, which on a switchable-graphics laptop would cost far more than the reading is worth. The last reading
            // goes with it: one taken before the card slept says nothing about an interval that ends after it woke.
            _previous = null;
            return 0;
        }

        if (_sysman.GetEnergyCounter(_domain, out var now) != Success) return Stopped();

        if (_previous is not { } before || now.Microjoules < before.Microjoules || now.Microseconds < before.Microseconds)
        {
            // The first reading, or a counter that was reset or wrapped: this one is the start of the next interval.
            _previous = now;
            return null;
        }
        if (now.Microseconds == before.Microseconds)
        {
            // The driver has taken no new reading since the last tick, so only a card that is off is known to draw nothing.
            return now.Microjoules == before.Microjoules ? Stopped() : null;
        }

        _previous = now;
        return (now.Microjoules - before.Microjoules) / (double)(now.Microseconds - before.Microseconds);
    }

    /// <summary>Nothing, for a card that is on and did not answer; 0 W for one Windows now says it has switched off,
    /// which is a card that went to sleep during the tick.</summary>
    private double? Stopped() => _poweredOff() ? 0 : null;

    /// <param name="Device">The card Level Zero found, which names it to Windows by its PCI ids.</param>
    /// <param name="Domain">The power domain read every tick.</param>
    /// <param name="Scope">What that domain's watts cover.</param>
    private readonly record struct Card(SysmanDevice Device, IntPtr Domain, GpuPowerScope Scope);
}
