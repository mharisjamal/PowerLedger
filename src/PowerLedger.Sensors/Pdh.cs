using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>
/// One Performance Data Helper query: its counters are added once and then read together, with one collection a tick.
/// A <see cref="System.Diagnostics.PerformanceCounter"/> runs a whole query of its own through the registry each time its
/// RawValue is read, so four rails read that way cost four queries a tick; one query holds them all and reads them at once.
/// Counters are named by their English paths, so a machine in any language answers. Single-threaded: the sampling loop
/// owns it.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    /// <summary>PDH_CSTATUS_VALID_DATA and PDH_CSTATUS_NEW_DATA: the counter has a value.</summary>
    private const uint ValidData = 0;
    private const uint NewData = 1;

    private readonly List<IntPtr> _counters = [];
    private IntPtr _query;

    /// <summary>Opens an empty query. Throws a <see cref="Win32Exception"/> when Windows will not open one.</summary>
    public PdhQuery()
    {
        var status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0) throw Failure(status, "would not open a performance counter query");
    }

    /// <summary>Adds a counter by its English path, as <c>\Energy Meter(RAPL_Package0_PKG)\Energy</c>, and gives the number it
    /// is read by; null when Windows has no such counter.</summary>
    public int? TryAdd(string path)
    {
        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) != 0) return null;
        _counters.Add(counter);
        return _counters.Count - 1;
    }

    /// <summary>Takes one reading of every counter in the query. Throws a <see cref="Win32Exception"/> when Windows will not
    /// answer.</summary>
    public void Collect()
    {
        var status = PdhCollectQueryData(_query);
        if (status != 0) throw Failure(status, "would not read its performance counters");
    }

    /// <summary>A counter's raw value as the last collection found it, or null when it had none.</summary>
    public long? Raw(int counter)
    {
        if (PdhGetRawCounterValue(_counters[counter], out _, out var value) != 0) return null;
        return value.CStatus is ValidData or NewData ? value.FirstValue : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counters.Clear();
    }

    private static Win32Exception Failure(uint status, string what)
        => new(unchecked((int)status), FormattableString.Invariant($"Windows {what} (PDH error 0x{status:X8})"));

    /// <summary>PDH_RAW_COUNTER: CStatus, a FILETIME, then FirstValue on its 8-byte boundary, SecondValue and MultiCount.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct RawCounter
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(16)] public long FirstValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetRawCounterValue(IntPtr counter, out uint type, out RawCounter value);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
