using System.ComponentModel;
using System.Diagnostics;

namespace PowerLedger.Sensors;

/// <summary>
/// Discrete GPU presence and load for an AMD or Intel card. Windows' GPU Engine counters give the load, and the power
/// model turns it into watts with the card's rated power wherever the card's own library has not measured them, as
/// <see cref="AmdSource"/> does on the cards that can. This source fills no watts of its own, so a measured reading
/// stands. The counters are read at most every <see cref="ReadEvery"/>, because a read walks every process on every
/// engine, and the load is held in between. The machine's sensor set adds this source only when the NVIDIA source has
/// nothing to say, since NVIDIA's own library already gives the load.
/// </summary>
public sealed class GpuLoadSource : ISensorSource
{
    /// <summary>How often the counters are read; the ticks in between carry the last load.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);

    private const string NoCard = "no AMD or Intel discrete GPU";

    private readonly Func<IReadOnlyList<GpuAdapter>> _adapters;
    private readonly Func<EngineSnapshot> _read;
    private readonly Func<TimeSpan> _clock;
    private readonly DxgiAdapters? _dxgi;
    private GpuAdapter? _card;
    private EngineSnapshot? _previous;
    private TimeSpan? _readAt;
    private double? _load;

    /// <summary>Finds the card through DXGI and checks that Windows publishes the counters.</summary>
    public GpuLoadSource() : this(new DxgiAdapters())
    {
    }

    private GpuLoadSource(DxgiAdapters dxgi) : this(dxgi.Current, new GpuEngines().Read, Elapsed(), () => GpuEngines.Published)
    {
        _dxgi = dxgi;
        if (!Supported) dxgi.Dispose();     // nothing to ask later, so nothing to keep
    }

    /// <summary>Test seam: any list of adapters, source of snapshots and clock.</summary>
    /// <param name="published">Whether Windows publishes the GPU Engine counters; yes when left out.</param>
    internal GpuLoadSource(
        Func<IReadOnlyList<GpuAdapter>> adapters, Func<EngineSnapshot> read, Func<TimeSpan> clock, Func<bool>? published = null)
    {
        _adapters = adapters;
        _read = read;
        _clock = clock;
        Unavailable = Probe(published ?? (static () => true));
        Supported = Unavailable is null;
    }

    public string Name => "gpu-load";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (!Supported) return;
        var now = _clock();
        if (_readAt is not { } last || now - last >= ReadEvery)
        {
            Read();
            _readAt = now;
        }
        if (_card is null) return;
        draft.DGpuPresent = true;
        draft.DGpuLoad = _load;                 // and never the watts, which a vendor's library may already have measured
    }

    public void Dispose() => _dxgi?.Dispose();

    /// <summary>A new snapshot and the load since the last one. A restarted driver gives the card a new LUID, so a card
    /// that changed starts over rather than comparing one adapter's counts with another's.</summary>
    private void Read()
    {
        var card = DiscreteGpu.Choose(_adapters());
        if (card?.CounterLuid != _card?.CounterLuid)
        {
            _previous = null;
            _load = null;
        }
        _card = card;
        if (card is null) return;

        var snapshot = _read();
        _load = _previous is { } before ? GpuEngines.Busiest(before, snapshot, card.CounterLuid) : null;
        _previous = snapshot;
    }

    /// <summary>Why the source cannot read this machine, or null when it can. Windows declining to answer makes the source
    /// unsupported rather than throwing, because a constructor that threw would stop the whole sensor set from being built.</summary>
    private string? Probe(Func<bool> published)
    {
        try
        {
            _card = DiscreteGpu.Choose(_adapters());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A DXGI failure arrives as whichever exception its HRESULT maps to, so none is singled out.
            return "Windows would not list the graphics adapters";
        }
        if (_card is null) return NoCard;

        try
        {
            return published() ? null : "Windows publishes no GPU load counters";
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            return error.Message;
        }
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
}
