using System.ComponentModel;
using System.Diagnostics;

namespace PowerLedger.Sensors;

/// <summary>
/// Every discrete card's presence and load from Windows' GPU Engine counters, for whatever the cards' own libraries
/// could not say. Windows keeps the counters for every maker's card. Each candidate adapter (see
/// <see cref="DiscreteGpu.IsCandidate"/>) that a library already added this tick, recognised by its PCI ids, is given
/// its name, rating and load where the library gave none, as an AMD card whose library measures only its watts, or an
/// NVIDIA card whose NVML gives no utilisation. Each one no library added, an NVIDIA card without NVML or another maker's
/// card, is added here with its load and its rating, and the power model turns the load into watts. No watts are written
/// over a library's, so a measured card keeps its own figure and none is counted twice. A card only this source reads and
/// Windows has switched off draws nothing. The counters are read at most every <see cref="ReadEvery"/>, because a read walks
/// every process on every engine, and the loads are held in between.
/// </summary>
public sealed class GpuLoadSource : ISensorSource
{
    /// <summary>How often the counters are read; the ticks in between carry the last loads.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);

    private const string NoCard = "no discrete GPU";

    private readonly Func<IReadOnlyList<GpuAdapter>> _adapters;
    private readonly Func<EngineSnapshot> _read;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<GpuAdapter, Func<bool>> _powerState;
    private readonly Dictionary<string, Func<bool>> _poweredOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly DxgiAdapters? _dxgi;
    private IReadOnlyList<GpuAdapter> _cards = [];
    private Dictionary<string, double?> _loads = new(StringComparer.OrdinalIgnoreCase);
    private EngineSnapshot? _previous;
    private TimeSpan? _readAt;

    /// <summary>Finds the cards through DXGI and checks that Windows publishes the counters.</summary>
    public GpuLoadSource() : this(new DxgiAdapters())
    {
    }

    private GpuLoadSource(DxgiAdapters dxgi) : this(dxgi.Current, new GpuEngines().Read, Elapsed(), () => GpuEngines.Published, WindowsPowerState)
    {
        _dxgi = dxgi;
        if (!Supported) dxgi.Dispose();     // nothing to ask later, so nothing to keep
    }

    /// <summary>Test seam: any list of adapters, source of snapshots, clock and power state.</summary>
    /// <param name="published">Whether Windows publishes the GPU Engine counters; yes when left out.</param>
    /// <param name="powerState">Whether Windows has a card switched off; never, when left out.</param>
    internal GpuLoadSource(
        Func<IReadOnlyList<GpuAdapter>> adapters, Func<EngineSnapshot> read, Func<TimeSpan> clock, Func<bool>? published = null,
        Func<GpuAdapter, Func<bool>>? powerState = null)
    {
        _adapters = adapters;
        _read = read;
        _clock = clock;
        _powerState = powerState ?? (static _ => static () => false);
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

        var taken = new HashSet<GpuCardDraft>();
        foreach (var adapter in _cards)
        {
            var load = _loads.GetValueOrDefault(adapter.CounterLuid);
            var card = Recognise(draft, adapter, taken);
            if (card is null)
            {
                card = draft.AddGpu(adapter.VendorId, adapter.DeviceId);
                if (PoweredOff(adapter))
                {
                    card.Watts = 0;
                    load = 0;
                }
            }
            taken.Add(card);
            card.Name ??= adapter.Description;
            card.Rating ??= TdpTable.Bundled.GpuOrGuess(adapter.Description, adapter.DedicatedBytes);
            card.Load ??= load;                 // and never the watts, which the card's library may already have measured
        }
    }

    public void Dispose() => _dxgi?.Dispose();

    /// <summary>The card a library already added for this adapter: the first not yet taken with the adapter's vendor and,
    /// where both know it, its device id. Two identical cards are told apart only by order, which cannot change the sum:
    /// both are rated alike.</summary>
    private static GpuCardDraft? Recognise(SampleDraft draft, GpuAdapter adapter, HashSet<GpuCardDraft> taken)
        => draft.Gpus.FirstOrDefault(card => !taken.Contains(card)
            && card.VendorId == adapter.VendorId
            && (card.DeviceId == 0 || adapter.DeviceId == 0 || card.DeviceId == adapter.DeviceId));

    /// <summary>A new snapshot and each card's load since the last one. A restarted driver gives a card a new LUID, so a card
    /// that was not in the last read starts over rather than comparing one adapter's counts with another's.</summary>
    private void Read()
    {
        var before = _cards.Select(card => card.CounterLuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _cards = DiscreteGpu.Candidates(_adapters());
        if (_cards.Count == 0)
        {
            _previous = null;
            _loads = new(StringComparer.OrdinalIgnoreCase);
            return;
        }

        var snapshot = _read();
        var loads = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _cards)
        {
            loads[card.CounterLuid] = _previous is { } earlier && before.Contains(card.CounterLuid)
                ? GpuEngines.Busiest(earlier, snapshot, card.CounterLuid)
                : null;
        }
        _loads = loads;
        _previous = snapshot;
    }

    /// <summary>Whether Windows has the card switched off, asked once per card of the display adapter with its PCI ids.
    /// Anything that cannot answer counts as on, which never hides real draw.</summary>
    private bool PoweredOff(GpuAdapter adapter)
    {
        try
        {
            if (!_poweredOff.TryGetValue(adapter.CounterLuid, out var ask))
            {
                ask = _powerState(adapter);
                _poweredOff[adapter.CounterLuid] = ask;
            }
            return ask();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Why the source cannot read this machine, or null when it can. Windows declining to answer makes the source
    /// unsupported rather than throwing, because a constructor that threw would stop the whole sensor set from being built.</summary>
    private string? Probe(Func<bool> published)
    {
        try
        {
            _cards = DiscreteGpu.Candidates(_adapters());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A DXGI failure arrives as whichever exception its HRESULT maps to, so none is singled out.
            return "Windows would not list the graphics adapters";
        }
        if (_cards.Count == 0) return NoCard;

        try
        {
            return published() ? null : "Windows publishes no GPU load counters";
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            return error.Message;
        }
    }

    private static Func<bool> WindowsPowerState(GpuAdapter adapter)
    {
        if (adapter.DeviceId == 0) return static () => false;
        var found = DevicePowerState.FindDisplayAdapter(FormattableString.Invariant($@"PCI\VEN_{adapter.VendorId:X4}&DEV_{adapter.DeviceId:X4}"));
        return found is null ? static () => false : () => DevicePowerState.IsPoweredOff(found);
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
}
