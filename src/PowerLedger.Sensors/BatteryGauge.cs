using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// The laptop battery's charge in watt-hours (accuracy A10), from the same SYSTEM_BATTERY_STATE the battery source reads:
/// what is left and what it holds full, which the service reads as the machine goes to sleep and as it wakes to learn what
/// a sleep costs. Read only then, never per tick. A battery that is not the machine's own (a UPS) or that reports
/// relative units rather than milliwatt-hours gives none.
/// </summary>
public static class BatteryGauge
{
    /// <summary>A reader of this machine's battery: whether its batteries are short-term or report relative units is asked
    /// once, on the first read.</summary>
    public static Func<BatteryCharge?> Windows()
    {
        var shortTerm = new Lazy<bool>(Win32.BatteriesAreShortTerm);
        var relative = new Lazy<bool>(BatteryUnits.Relative);
        return () => From(Win32.ReadBatteryState(shortTerm.Value), relative.Value);
    }

    /// <summary>The charge a battery state gives; null when it gives none in watt-hours.</summary>
    internal static BatteryCharge? From(Win32.BatteryState? state, bool relativeUnits)
    {
        if (state is not { OwnBattery: true } battery || relativeUnits) return null;
        if (battery.RemainingMilliwattHours == Win32.UnknownCapacity || battery.FullMilliwattHours is Win32.UnknownCapacity or 0) return null;
        return new BatteryCharge(battery.AcOnLine, battery.RemainingMilliwattHours / 1000.0, battery.FullMilliwattHours / 1000.0);
    }
}
