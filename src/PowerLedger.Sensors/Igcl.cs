using System.Runtime.InteropServices;
using System.Text;

namespace PowerLedger.Sensors;

/// <summary>One graphics adapter as Intel's Graphics Control Library describes it.</summary>
/// <param name="Handle">The library's handle for it.</param>
/// <param name="Integrated">Graphics built into the processor, whose watts the processor's package reading already counts.</param>
internal readonly record struct IgclAdapter(IntPtr Handle, uint VendorId, uint DeviceId, bool Integrated, string Name);

/// <summary>One snapshot of an adapter's power telemetry: the card's and the chip's monotonic energy counters in joules and
/// the time they were taken in seconds, each null where the adapter doesn't report it.</summary>
internal readonly record struct IgclTelemetry(double? CardJoules, double? GpuJoules, double? Seconds);

/// <summary>
/// The Graphics Control Library calls <see cref="IntelCardSource"/> makes, one method per call, so a test can stand in for
/// the driver. Each returns that call's ctl_result_t, 0 for success, and may throw what a missing library throws.
/// </summary>
internal interface IIgcl : IDisposable
{
    /// <summary>ctlInit.</summary>
    int Init();

    /// <summary>ctlEnumerateDevices, then ctlGetDeviceProperties for each.</summary>
    int Adapters(out IgclAdapter[] adapters);

    /// <summary>ctlPowerTelemetryGet.</summary>
    int Telemetry(IntPtr adapter, out IgclTelemetry telemetry);
}

/// <summary>
/// Intel's Graphics Control Library (IGCL), which ships with Intel's graphics driver as ControlLib.dll. It is loaded only
/// where the driver put it; where it isn't, the first call throws <see cref="DllNotFoundException"/> and the Arc source
/// falls back to Level Zero. Only reading calls are made. The structures are those of igcl_api.h (API 1.1), laid out for
/// 64-bit Windows, the only kind PowerLedger ships for this library; offsets that are not read are left as room for the
/// driver to write into. Protocol facts only: nothing here is Intel's code.
/// </summary>
internal sealed class ControlLibrary : IIgcl
{
    private const string Library = "ControlLib.dll";
    private const int Success = 0;

    /// <summary>CTL_RESULT_ERROR_UNSUPPORTED_VERSION, CTL_RESULT_ERROR_INVALID_SIZE and CTL_RESULT_ERROR_UNSUPPORTED_SIZE:
    /// a driver older than the telemetry structure's version 1, which is then asked for version 0.</summary>
    private static readonly int[] OlderStructure = [0x40000009, 0x4000000f, 0x40000010];

    /// <summary>ctl_init_args_t: Size, Version, AppVersion, flags, SupportedVersion and a 16-byte application id.</summary>
    private const int InitArgsSize = 36;

    /// <summary>CTL_MAKE_VERSION(1, 1).</summary>
    private const int AppVersion = (1 << 16) | 1;

    /// <summary>CTL_INIT_FLAG_USE_LEVEL_ZERO, as Intel's telemetry sample starts the library.</summary>
    private const int UseLevelZero = 1;

    /// <summary>sizeof(ctl_device_adapter_properties_t) on 64-bit Windows, asked for at version 2.</summary>
    private const int PropertiesSize = 320;

    /// <summary>sizeof(ctl_power_telemetry_t) at version 1, and up to the fan speeds, which is all version 0 has.</summary>
    private const int TelemetrySize = 1024;
    private const int TelemetrySizeV0 = 808;

    /// <summary>Offsets in ctl_power_telemetry_t of the items read: each a ctl_oc_telemetry_item_t of 24 bytes.</summary>
    private const int TimeStampAt = 8;
    private const int GpuEnergyAt = 32;
    private const int CardEnergyAt = 384;

    /// <summary>CTL_UNITS_ENERGY_JOULES and CTL_UNITS_TIME_SECONDS.</summary>
    private const int Joules = 6;
    private const int Seconds = 7;

    /// <summary>CTL_ADAPTER_PROPERTIES_FLAG_INTEGRATED.</summary>
    private const uint IntegratedFlag = 1;

    private IntPtr _api;
    private bool _versionZero;

    public int Init()
    {
        var args = Marshal.AllocHGlobal(InitArgsSize);
        try
        {
            Clear(args, InitArgsSize);
            Marshal.WriteInt32(args, 0, InitArgsSize);
            Marshal.WriteInt32(args, 8, AppVersion);
            Marshal.WriteInt32(args, 12, UseLevelZero);
            return ctlInit(args, out _api);
        }
        finally
        {
            Marshal.FreeHGlobal(args);
        }
    }

    public int Adapters(out IgclAdapter[] adapters)
    {
        adapters = [];
        uint count = 0;
        var result = ctlEnumerateDevices(_api, ref count, null);
        if (result != Success || count == 0) return result;
        var handles = new IntPtr[count];
        result = ctlEnumerateDevices(_api, ref count, handles);
        if (result != Success) return result;

        var found = new List<IgclAdapter>((int)count);
        var properties = Marshal.AllocHGlobal(PropertiesSize);
        var luid = Marshal.AllocHGlobal(8);
        try
        {
            foreach (var handle in handles.Take((int)count))
            {
                Clear(properties, PropertiesSize);
                Marshal.WriteInt32(properties, 0, PropertiesSize);
                Marshal.WriteByte(properties, 4, 2);
                Marshal.WriteIntPtr(properties, 8, luid);
                Marshal.WriteInt32(properties, 16, 8);
                if (ctlGetDeviceProperties(handle, properties) != Success) continue;
                var vendor = (uint)Marshal.ReadInt32(properties, 64);
                var device = (uint)Marshal.ReadInt32(properties, 68);
                var name = new byte[100];
                Marshal.Copy(properties + 88, name, 0, name.Length);
                var flags = (uint)Marshal.ReadInt32(properties, 188);
                var end = Array.IndexOf(name, (byte)0);
                var text = Encoding.ASCII.GetString(name, 0, end >= 0 ? end : name.Length);
                found.Add(new IgclAdapter(handle, vendor, device, (flags & IntegratedFlag) != 0, text.Trim()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(luid);
            Marshal.FreeHGlobal(properties);
        }
        adapters = [.. found];
        return Success;
    }

    public int Telemetry(IntPtr adapter, out IgclTelemetry telemetry)
    {
        telemetry = default;
        var buffer = Marshal.AllocHGlobal(TelemetrySize);
        try
        {
            var result = Ask(adapter, buffer);
            if (!_versionZero && OlderStructure.Contains(result))
            {
                _versionZero = true;
                result = Ask(adapter, buffer);
            }
            if (result != Success) return result;
            telemetry = new IgclTelemetry(
                Item(buffer, CardEnergyAt, Joules), Item(buffer, GpuEnergyAt, Joules), Item(buffer, TimeStampAt, Seconds));
            return Success;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_api == IntPtr.Zero) return;
        try
        {
            _ = ctlClose(_api);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing was opened.
        }
        _api = IntPtr.Zero;
    }

    private int Ask(IntPtr adapter, IntPtr buffer)
    {
        Clear(buffer, TelemetrySize);
        Marshal.WriteInt32(buffer, 0, _versionZero ? TelemetrySizeV0 : TelemetrySize);
        Marshal.WriteByte(buffer, 4, (byte)(_versionZero ? 0 : 1));
        return ctlPowerTelemetryGet(adapter, buffer);
    }

    /// <summary>A ctl_oc_telemetry_item_t: whether it is supported, its units, its data type, and its value at offset 16, as
    /// a number, or null when it isn't supported or isn't in the units expected.</summary>
    private static double? Item(IntPtr telemetry, int at, int units)
    {
        if (Marshal.ReadByte(telemetry, at) == 0 || Marshal.ReadInt32(telemetry, at + 4) != units) return null;
        var value = telemetry + at + 16;
        double? number = Marshal.ReadInt32(telemetry, at + 8) switch
        {
            0 => (sbyte)Marshal.ReadByte(value),
            1 => Marshal.ReadByte(value),
            2 => Marshal.ReadInt16(value),
            3 => (ushort)Marshal.ReadInt16(value),
            4 => Marshal.ReadInt32(value),
            5 => (uint)Marshal.ReadInt32(value),
            6 => Marshal.ReadInt64(value),
            7 => (ulong)Marshal.ReadInt64(value),
            8 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(value)),
            9 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(value)),
            _ => null,
        };
        return number is { } n && double.IsFinite(n) ? n : null;
    }

    private static void Clear(IntPtr memory, int length)
    {
        for (var offset = 0; offset < length; offset++) Marshal.WriteByte(memory, offset, 0);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ctlInit(IntPtr args, out IntPtr api);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ctlClose(IntPtr api);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ctlEnumerateDevices(IntPtr api, ref uint count, [Out] IntPtr[]? devices);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ctlGetDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ctlPowerTelemetryGet(IntPtr device, IntPtr telemetry);
}
