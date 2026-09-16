using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>The power model for the current settings and hardware.</summary>
internal static class ModelFactory
{
    /// <summary>TDPs come from the bundled table through the inventory; a part the table does not know gets the chassis
    /// default. The external monitors draw what <paramref name="monitors"/> says.</summary>
    public static PowerModel Build(ServiceSettings settings, InventoryFacts facts, IBaselineProvider baselines, IMonitorDraw monitors)
    {
        var defaults = settings.Profile.Chassis == ChassisKind.Laptop ? HardwareFacts.LaptopDefaults : HardwareFacts.DesktopDefaults;
        var hardware = new HardwareFacts(facts.CpuTdpW ?? defaults.CpuTdpW, facts.GpuTdpW ?? defaults.GpuTdpW);
        return new PowerModel(
            settings.Profile, hardware, new PowerModelOptions(IdleThresholdSeconds: settings.IdleThresholdSeconds), baselines, monitors);
    }
}
