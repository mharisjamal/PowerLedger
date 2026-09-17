using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>One adapter as the fake ADL describes it.</summary>
internal sealed class FakeAdlAdapter
{
    /// <summary>The ADL index handle, which is not the adapter's place in the list.</summary>
    public int Index { get; init; } = 4;

    /// <summary>Below zero for an adapter Windows remembers but no longer has.</summary>
    public int Bus { get; init; } = 3;

    public string Udid { get; init; } = "PCI_VEN_1002&DEV_747E&SUBSYS_54021462&REV_C8_4&1A2B3C4D&0&0008A";

    public string Pnp { get; init; } = @"PCI\VEN_1002&DEV_747E&SUBSYS_54021462&REV_C8\4&1A2B3C4D&0&0008A";

    /// <summary>What ADL's own vendor field holds, which the source only falls back on.</summary>
    public int VendorId { get; init; } = 0x1002;

    /// <summary>ADL_ASIC_DISCRETE. Integrated graphics report ADL_ASIC_INTEGRATED, which is 2, or Fusion, which is 32.</summary>
    public int AsicTypes { get; init; } = 1;

    public int AsicValids { get; init; } = 0xFF;

    /// <summary>ADL_PMLOG_BOARD_POWER in whole watts, or null where the card does not report it.</summary>
    public int? BoardWatts { get; set; }

    /// <summary>ADL_PMLOG_ASIC_POWER in whole watts, or null where the card does not report it.</summary>
    public int? ChipWatts { get; set; }
}

/// <summary>
/// ADL as a set of functions in unmanaged memory, which fill the caller's buffers at the offsets adl_structures.h
/// gives: AdapterInfo is 1,572 bytes of eight integers and five 256-byte strings, and ADLPMLogDataOutput is a size
/// followed by 256 pairs of an is-supported flag and a value. Writing them by offset here, rather than through the
/// source's own structures, is what makes the layout a test and not an assumption.
/// </summary>
internal sealed unsafe class FakeAdl : IDisposable
{
    private const int AdapterSize = 1572;
    private const int IndexAt = 4, UdidAt = 8, BusAt = 264, DeviceAt = 268, FunctionAt = 272, VendorAt = 276, PnpAt = 1312;
    private const int SensorsAt = 4, SensorPair = 8;

    [ThreadStatic]
    private static FakeAdl? _live;

    private readonly IReadOnlyList<FakeAdlAdapter> _adapters;
    private GCHandle _self;

    public FakeAdl(params FakeAdlAdapter[] adapters)
    {
        _adapters = adapters;
        _self = GCHandle.Alloc(this);
        _live = this;
    }

    /// <summary>The result ADL2_Main_Control_Create gives.</summary>
    public int CreateResult { get; set; }

    /// <summary>The result ADL2_New_QueryPMLogData_Get gives.</summary>
    public int ReadResult { get; set; }

    /// <summary>How many contexts were made and destroyed.</summary>
    public int Creates { get; private set; }

    public int Destroys { get; private set; }

    /// <summary>How many times the performance metrics were asked for.</summary>
    public int Reads { get; private set; }

    /// <summary>True once the allocator ADL was handed answered with memory, as ADL needs it to.</summary>
    public bool AllocatorWorks { get; private set; }

    public AdlLibrary Library => new(&Create, &Destroy, &NumberOfAdapters, &AdapterInfo, &AsicFamily, &QueryPmLog);

    public void Dispose()
    {
        if (_self.IsAllocated) _self.Free();
        if (ReferenceEquals(_live, this)) _live = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Create(delegate* unmanaged[Stdcall]<int, nint> allocate, int connectedOnly, nint* context)
    {
        var fake = _live ?? throw new InvalidOperationException("no fake ADL on this thread");
        var block = allocate(64);
        if (block != 0)
        {
            fake.AllocatorWorks = true;
            NativeMemory.Free((void*)block);
        }
        if (fake.CreateResult != 0 || connectedOnly != 1)
        {
            *context = 0;
            return fake.CreateResult != 0 ? fake.CreateResult : -1;
        }
        fake.Creates++;
        *context = GCHandle.ToIntPtr(fake._self);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Destroy(nint context)
    {
        Of(context).Destroys++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NumberOfAdapters(nint context, int* adapters)
    {
        *adapters = Of(context)._adapters.Count;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AdapterInfo(nint context, nint list, int size)
    {
        var fake = Of(context);
        if (size < fake._adapters.Count * AdapterSize) return -9;           // ADL_ERR_NULL_POINTER
        for (var index = 0; index < fake._adapters.Count; index++)
        {
            var adapter = fake._adapters[index];
            var record = (byte*)(list + (index * AdapterSize));
            Write(record, 0, AdapterSize);
            Write(record, IndexAt, adapter.Index);
            Text(record + UdidAt, adapter.Udid);
            Write(record, BusAt, adapter.Bus);
            Write(record, DeviceAt, 0);
            Write(record, FunctionAt, 0);
            Write(record, VendorAt, adapter.VendorId);
            Text(record + PnpAt, adapter.Pnp);
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AsicFamily(nint context, int adapter, int* types, int* valids)
    {
        if (Found(Of(context), adapter) is not { } card) return -5;         // ADL_ERR_INVALID_ADL_IDX
        *types = card.AsicTypes;
        *valids = card.AsicValids;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryPmLog(nint context, int adapter, nint log)
    {
        var fake = Of(context);
        fake.Reads++;
        if (fake.ReadResult != 0) return fake.ReadResult;
        if (Found(fake, adapter) is not { } card) return -5;

        var bytes = (byte*)log;
        Write(bytes, 0, 2052);
        Sensor(bytes, 23, card.ChipWatts);
        Sensor(bytes, 73, card.BoardWatts);
        return 0;
    }

    private static FakeAdl Of(nint context) => (FakeAdl)GCHandle.FromIntPtr(context).Target!;

    private static FakeAdlAdapter? Found(FakeAdl fake, int adapter)
        => fake._adapters.FirstOrDefault(a => a.Index == adapter);

    private static void Sensor(byte* log, int sensor, int? watts)
    {
        Write(log, SensorsAt + (sensor * SensorPair), watts is null ? 0 : 1);
        Write(log, SensorsAt + (sensor * SensorPair) + 4, watts ?? 0);
    }

    private static void Write(byte* target, int offset, int value) => *(int*)(target + offset) = value;

    private static void Text(byte* target, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        for (var index = 0; index < bytes.Length && index < 255; index++) target[index] = bytes[index];
        target[Math.Min(bytes.Length, 255)] = 0;
    }
}
