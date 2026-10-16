using System.ComponentModel;
using System.Security;
using Microsoft.Win32;
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>Each process's time on the graphics engines, for the Apps page (Plan AE), where the sensors do not read the load
/// counters themselves.</summary>
public interface IEngineCounters
{
    /// <summary>Each process's time on the cards' engines and on the processor's graphics' since the last read; null on the
    /// first read, when Windows will not give the counters, and when nothing tells a card's engines apart.</summary>
    GraphicsWork? Read();
}

/// <summary>
/// Windows' GPU Engine counters, read for the Apps page alone where <see cref="GpuLoadSource"/> does not read them: a PC whose
/// every card NVIDIA's library reads, which is most PCs with an NVIDIA card. Its watts then go by each process's time on its
/// engines (Plan AE follow-up) rather than by the processor's split. Listing the adapters through DXGI to tell them apart
/// would wake a card Windows has switched off, which is why the load counters are not read there; Windows' DirectX registry
/// (<see cref="DirectXAdapters"/>) names each adapter's LUID with no adapter asked, and reading the counters leaves a
/// switched-off card off. The registry is asked again only when the counters show an adapter it was not asked about. The
/// caller reads at the pace of its looks, every five seconds at most. Single-threaded.
/// </summary>
public sealed class EngineCounters : IEngineCounters
{
    private readonly Func<EngineSnapshot> _read;
    private readonly Func<IReadOnlyList<GpuAdapter>> _adapters;
    private readonly Func<bool> _published;
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private string[] _cards = [];
    private string[] _processorGraphics = [];
    private EngineSnapshot? _previous;
    private bool? _isPublished;

    /// <summary>Whether the registry names a card among the adapters the counters show; null until it was asked.</summary>
    public bool? HasCard { get; private set; }

    /// <summary>The GPU Engine counters and the DirectX registry.</summary>
    public EngineCounters()
        : this(new GpuEngines().Read, DirectXAdapters.Read, static () => GpuEngines.Published)
    {
    }

    /// <summary>Test seam: any snapshots and adapters.</summary>
    /// <param name="published">Whether Windows publishes the counters; yes when left out.</param>
    internal EngineCounters(Func<EngineSnapshot> read, Func<IReadOnlyList<GpuAdapter>> adapters, Func<bool>? published = null)
    {
        _read = read;
        _adapters = adapters;
        _published = published ?? (static () => true);
    }

    public GraphicsWork? Read()
    {
        if (!Published()) return null;
        EngineSnapshot snapshot;
        try
        {
            snapshot = _read();
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception or FormatException)
        {
            _previous = null;                                       // the next read starts over
            return null;
        }
        if (HasUnaskedAdapter(snapshot)) Classify(snapshot);
        var work = _previous is { } before && _cards.Length > 0 ? GpuEngines.Work(before, snapshot, _cards, _processorGraphics) : null;
        _previous = snapshot;
        return work;
    }

    private bool Published()
    {
        try
        {
            return _isPublished ??= _published();
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            return (_isPublished = false).Value;
        }
    }

    /// <summary>True when the counters show an adapter the registry has not been asked about.</summary>
    private bool HasUnaskedAdapter(EngineSnapshot snapshot)
    {
        foreach (var instance in snapshot.RunningTime.Keys)
        {
            if (GpuEngines.AdapterOf(instance) is { } luid && !_asked.Contains(luid)) return true;
        }
        return false;
    }

    /// <summary>The cards and the processor's graphics, as <see cref="GpuLoadSource"/> tells them apart, by the registry; the
    /// adapters <paramref name="snapshot"/> shows count as asked about, known to it or not.</summary>
    private void Classify(EngineSnapshot snapshot)
    {
        IReadOnlyList<GpuAdapter> adapters;
        try
        {
            adapters = _adapters();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            adapters = [];
        }
        _cards = [.. DiscreteGpu.Candidates(adapters).Select(card => card.CounterLuid)];
        _processorGraphics = [.. GpuLoadSource.ProcessorGraphics(adapters).Select(graphics => graphics.CounterLuid)];
        HasCard = _cards.Length > 0;
        foreach (var instance in snapshot.RunningTime.Keys)
        {
            if (GpuEngines.AdapterOf(instance) is { } luid) _asked.Add(luid);
        }
    }
}

/// <summary>
/// The graphics adapters as Windows' DirectX registry lists them (HKLM\SOFTWARE\Microsoft\DirectX), each with its LUID,
/// name, PCI ids and dedicated memory as DXGI gives them, with no adapter asked: listing them through DXGI wakes a card
/// Windows has switched off. Windows writes an entry for each adapter as it starts; where an adapter gone since an earlier
/// boot left one with the same LUID, the one Windows saw last wins.
/// </summary>
public static class DirectXAdapters
{
    private const string Key = @"SOFTWARE\Microsoft\DirectX";

    /// <summary>Every adapter the registry lists; none when it lists none or will not be read.</summary>
    public static IReadOnlyList<GpuAdapter> Read()
    {
        using var root = Registry.LocalMachine.OpenSubKey(Key);
        if (root is null) return [];
        List<Entry> entries = [];
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("AdapterLuid") is not long luid) continue;
            entries.Add(new Entry(
                key.GetValue("Description") as string ?? "", Dword(key, "VendorId"), Dword(key, "DeviceId"),
                unchecked((ulong)Qword(key, "DedicatedVideoMemory")), luid, Qword(key, "LastSeen")));
        }
        return From(entries);
    }

    /// <summary>One entry of the registry: <paramref name="Luid"/> is the LUID as one 64-bit number, its high part on top;
    /// <paramref name="LastSeen"/> when Windows last started the adapter, as a FILETIME.</summary>
    internal readonly record struct Entry(string Description, uint VendorId, uint DeviceId, ulong DedicatedBytes, long Luid, long LastSeen);

    internal static IReadOnlyList<GpuAdapter> From(IEnumerable<Entry> entries)
        => [.. entries.GroupBy(entry => entry.Luid).Select(same => same.MaxBy(entry => entry.LastSeen)).Select(entry => new GpuAdapter(
            entry.Description, entry.VendorId, entry.DedicatedBytes, unchecked((uint)entry.Luid), (int)(entry.Luid >> 32),
            Software: entry.VendorId == DiscreteGpu.MicrosoftVendor, entry.DeviceId))];

    private static uint Dword(RegistryKey key, string name) => key.GetValue(name) is int value ? unchecked((uint)value) : 0;

    private static long Qword(RegistryKey key, string name) => key.GetValue(name) is long value ? value : 0;
}
