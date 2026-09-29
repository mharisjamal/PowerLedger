using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// An Intel Arc card's watts, from the best reading its driver gives. Intel's Graphics Control Library (IGCL,
/// ctlPowerTelemetryGet) keeps a total card energy counter, which covers the chip, its memory, regulators and fans, so
/// where a discrete card reports it the card's watts are measured whole. Everywhere else, and whenever the library is
/// missing, won't start, finds no discrete card, or keeps failing, the card is read through Level Zero as before
/// (<see cref="ArcSource"/>), whose package domain leaves the rest of the card to the model.
///
/// Watts are the counter's joules between two readings over the seconds between them, on the library's own clock. The
/// library answers a card Windows has switched off with "device unavailable", and asking could wake it, so a card Windows
/// says is off draws nothing and isn't asked. Whether IGCL answers the service in session 0 is unknown; where it doesn't,
/// the library won't start or finds no card, and Level Zero reads the card, as it did before.
/// Never throws: whatever goes wrong leaves this tick's watts to the model.
/// </summary>
public sealed class IntelCardSource : ISensorSource
{
    /// <summary>CTL_RESULT_ERROR_DEVICE_UNAVAILABLE: the card is loading, unloading, asleep in D3 or recovering.</summary>
    internal const int DeviceUnavailable = 0x40000027;

    /// <summary>How many ticks in a row the library may fail before the card is handed to Level Zero for good.</summary>
    internal const int FailuresBeforeLevelZero = 5;

    private const int Success = 0;

    private readonly Func<ISensorSource> _makeLevelZero;
    private IIgcl? _igcl;
    private readonly IgclAdapter _card;
    private readonly Func<bool> _poweredOff = static () => false;
    private ISensorSource? _levelZero;
    private IgclTelemetry? _previous;
    private int _failures;

    /// <summary>Opens Intel's control library where the driver installed it, and Level Zero where it isn't any use.</summary>
    public IntelCardSource()
        : this(static () => new ControlLibrary(), static () => new ArcSource(), WindowsPowerState, Environment.Is64BitProcess)
    {
    }

    /// <summary>Test seam: any control library, any Level Zero source, any way of asking whether Windows has switched the
    /// card off, and the process's bitness.</summary>
    internal IntelCardSource(
        Func<IIgcl> igcl, Func<ISensorSource> levelZero, Func<IgclAdapter, Func<bool>> powerState, bool is64BitProcess = true)
    {
        _makeLevelZero = levelZero;
        if (is64BitProcess && Open(igcl, powerState, out _card, out _poweredOff, out var first) is { } opened)
        {
            _igcl = opened;
            _previous = first;               // the reading that showed the counter starts the first interval
            return;
        }
        _levelZero = levelZero();
    }

    /// <summary>Named as the Level Zero source is, since it reads the same card and the status screen lists it once.</summary>
    public string Name => "arc-gpu";

    public bool Supported => _igcl is not null || (_levelZero?.Supported ?? false);

    public string? Unavailable => _igcl is not null ? null : _levelZero?.Unavailable;

    public void Contribute(SampleDraft draft)
    {
        if (_igcl is not null && _failures >= FailuresBeforeLevelZero) HandToLevelZero();
        if (_igcl is null)
        {
            _levelZero?.Contribute(draft);
            return;
        }

        var card = draft.AddGpu(DiscreteGpu.IntelVendor, _card.DeviceId);
        card.Scope = GpuPowerScope.Board;
        try
        {
            card.Watts = Watts();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _failures++;
            card.Watts = null;
        }
    }

    public void Dispose()
    {
        Close(_igcl);
        _igcl = null;
        Close(_levelZero);
    }

    /// <summary>Whether Windows has the card switched off, asked of the display adapter with the card's PCI ids.</summary>
    private static Func<bool> WindowsPowerState(IgclAdapter adapter)
        => ArcSource.WindowsPowerState(new SysmanDevice(SysmanDevice.GpuType, adapter.VendorId, adapter.DeviceId, 0, 0));

    /// <summary>The library, started, with a discrete Intel card whose total card energy counter answers; null otherwise, with
    /// the library closed.</summary>
    private static IIgcl? Open(
        Func<IIgcl> make, Func<IgclAdapter, Func<bool>> powerState, out IgclAdapter card, out Func<bool> poweredOff, out IgclTelemetry first)
    {
        card = default;
        first = default;
        poweredOff = static () => false;
        IIgcl? igcl = null;
        try
        {
            igcl = make();
            if (igcl.Init() != Success || igcl.Adapters(out var adapters) != Success) return Closed(igcl);
            foreach (var adapter in adapters)
            {
                if (adapter.VendorId != DiscreteGpu.IntelVendor || adapter.Integrated) continue;
                var off = powerState(adapter);
                // A card asleep can't say whether it has the counter, and asking would wake it: Level Zero it is.
                if (off()) continue;
                if (igcl.Telemetry(adapter.Handle, out first) != Success || first.CardJoules is null || first.Seconds is null) continue;
                card = adapter;
                poweredOff = off;
                return igcl;
            }
            return Closed(igcl);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A library that is missing, too old or broken is Level Zero's turn, never a sensor set that won't build.
            return Closed(igcl);
        }
    }

    private static IIgcl? Closed(IIgcl? igcl)
    {
        Close(igcl);
        return null;
    }

    /// <summary>The card's watts since the last reading, or null when this tick can't say.</summary>
    private double? Watts()
    {
        if (_poweredOff())
        {
            // A reading from before the card slept says nothing about an interval that ends after it woke.
            _previous = null;
            return 0;
        }

        var result = _igcl!.Telemetry(_card.Handle, out var now);
        if (result == DeviceUnavailable)
        {
            _previous = null;
            return _poweredOff() ? 0 : null;
        }
        if (result != Success || now.CardJoules is not { } joules || now.Seconds is not { } seconds)
        {
            _failures++;
            return null;
        }
        _failures = 0;

        var before = _previous;
        _previous = now;
        if (before is not { CardJoules: { } earlierJoules, Seconds: { } earlierSeconds }) return null;
        if (joules < earlierJoules || seconds <= earlierSeconds) return null;   // reset, wrapped, or no new reading
        return (joules - earlierJoules) / (seconds - earlierSeconds);
    }

    private void HandToLevelZero()
    {
        Close(_igcl);
        _igcl = null;
        _previous = null;
        try
        {
            _levelZero = _makeLevelZero();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _levelZero = null;
        }
    }

    private static void Close(IDisposable? closeable)
    {
        try
        {
            closeable?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A library that won't close is the driver's business.
        }
    }
}
