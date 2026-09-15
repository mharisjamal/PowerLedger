using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>Windows' idle timeouts in the active power plan, plugged in and on battery. Zero means never; null means Windows did not say.</summary>
internal sealed record SleepTimeouts(TimeSpan? SleepAc, TimeSpan? SleepDc, TimeSpan? DisplayAc, TimeSpan? DisplayDc)
{
    public static SleepTimeouts Unknown { get; } = new(null, null, null, null);
}

internal interface ISleepSettings
{
    SleepTimeouts Read();
}

/// <summary>Reads the active power plan's sleep and display timeouts with powrprof, the API behind <c>powercfg /query</c> (spec §6).</summary>
internal sealed class SleepSettings : ISleepSettings
{
    private static readonly Guid SleepGroup = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    private static readonly Guid StandbyIdle = new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");
    private static readonly Guid VideoGroup = new("7516B95F-F776-4464-8C53-06167F40CC99");
    private static readonly Guid VideoIdle = new("3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E");

    public SleepTimeouts Read()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var scheme) != 0) return SleepTimeouts.Unknown;
        try
        {
            var plan = Marshal.PtrToStructure<Guid>(scheme);
            return new SleepTimeouts(
                Value(plan, SleepGroup, StandbyIdle, pluggedIn: true), Value(plan, SleepGroup, StandbyIdle, pluggedIn: false),
                Value(plan, VideoGroup, VideoIdle, pluggedIn: true), Value(plan, VideoGroup, VideoIdle, pluggedIn: false));
        }
        finally
        {
            LocalFree(scheme);
        }
    }

    private static TimeSpan? Value(Guid plan, Guid group, Guid setting, bool pluggedIn)
    {
        uint seconds;
        var result = pluggedIn
            ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref group, ref setting, out seconds)
            : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref group, ref setting, out seconds);
        return result == 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
