using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>Core and Sensors types turned into the Contracts shapes that travel on the pipe.</summary>
internal static class Frames
{
    /// <summary>The reading, plus which parts the tick's sample measured rather than modelled (Plan B's provenance gap), where
    /// its total came from, and what a measured GPU figure covered.</summary>
    public static ReadingFrame From(Reading r, Sample s) => new(
        r.Timestamp, r.DeltaSeconds, r.TotalW, r.Quality,
        new ComponentWatts(
            r.Components.Cpu, r.Components.Gpu, r.Components.Display, r.Components.Ram, r.Components.Storage,
            r.Components.Board, r.Components.Extras, r.Components.Monitors, r.Components.PsuLoss, r.Components.Unattributed),
        CpuMeasured: s.CpuPackageW is { } cpu && double.IsFinite(cpu),
        GpuMeasured: s.DGpuW is { } gpu && double.IsFinite(gpu),
        r.OnBattery, r.DisplayOn, r.UserIdle, r.SessionLocked, r.CpuLoad, r.GpuLoad, r.Brightness, r.Suspect,
        GpuScope: r.GpuScope, Total: r.TotalSource);

    /// <summary>The UPS and the power supply the sample read, for the status screen: each that gave a name or watts, with the
    /// watts and how they were found, or no watts and nothing said of them when it gave none that could be used.</summary>
    public static IReadOnlyList<PowerDeviceStatus> PowerDevices(Sample s)
    {
        var devices = new List<PowerDeviceStatus>(2);
        if (!string.IsNullOrWhiteSpace(s.UpsName) || Watts(s.UpsOutputW) is not null)
        {
            var how = s.UpsSource switch
            {
                UpsPowerSource.ActivePower => "real output power",
                UpsPowerSource.LoadOfRatedWatts => "load of its rated watts",
                UpsPowerSource.LoadOfRatedVoltAmps => "load of its rated VA, estimated",
                _ => "",
            };
            devices.Add(Device(PowerDeviceKind.Ups, s.UpsName, s.UpsOutputW, how));
        }
        if (!string.IsNullOrWhiteSpace(s.PsuName) || Watts(s.PsuOutputW) is not null || Watts(s.PsuWallW) is not null)
        {
            // A supply reports what it draws from the wall or what its rails put out, never both, and the screen has to
            // say which: one is the machine's whole draw and the other is that draw before the supply's own losses.
            var (watts, how) = Watts(s.PsuWallW) is { } wall
                ? (wall, "wall power, as the supply reports it")
                : (s.PsuOutputW, "DC output, all rails");
            devices.Add(Device(PowerDeviceKind.PowerSupply, s.PsuName, watts, how));
        }
        return devices;
    }

    public static SourceStatus From(SourceHealth health)
        => new(health.Name, health.Supported, health.Unavailable, health.Failures, health.LastError);

    private static PowerDeviceStatus Device(PowerDeviceKind kind, string? name, double? watts, string how)
        => Watts(watts) is { } read ? new(kind, name?.Trim() ?? "", read, how) : new(kind, name?.Trim() ?? "", null, "");

    /// <summary>Watts that can be shown: finite and not below zero. A number that isn't finite couldn't travel on the pipe.</summary>
    private static double? Watts(double? watts) => watts is { } w && double.IsFinite(w) && w >= 0 ? w : null;
}
