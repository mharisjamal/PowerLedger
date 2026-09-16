using PowerLedger.Contracts;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>The service settings, and the hardware hash the profile was last detected for, kept in the settings table.</summary>
internal sealed class SettingsStore(SettingsRepository settings)
{
    internal const string SettingsKey = "service.settings";
    internal const string ProfileHashKey = "service.profile-hash";

    /// <summary>The stored settings, or null on the first run or when the stored text cannot be read.</summary>
    public ServiceSettings? Load() => settings.Get(SettingsKey) is { } json ? PipeProtocol.DeserializeSettings(json) : null;

    public string? ProfileHash() => settings.Get(ProfileHashKey);

    public void Save(ServiceSettings value) => settings.Set(SettingsKey, PipeProtocol.SerializeSettings(value));

    public void SaveProfileHash(string hash) => settings.Set(ProfileHashKey, hash);
}

/// <summary>
/// When detection may touch the machine profile (Plan B handoff): on the first run, and when the hardware hash
/// changes, which means a different machine. Otherwise the stored profile, with the user's corrections, stands, except
/// that monitor settings from before monitors were detected pass to the monitors once they are.
/// </summary>
internal static class ProfilePolicy
{
    /// <summary>The figure every external monitor was given before the user typed one.</summary>
    private const double OldDefaultMonitorWatts = 25;

    /// <returns>The settings to use, and whether they differ from what was stored.</returns>
    public static (ServiceSettings Settings, bool Changed) Apply(ServiceSettings? stored, string? storedHash, InventoryFacts facts)
    {
        if (stored is not null && storedHash == facts.Hash) return (stored, false);
        var basis = stored?.Profile ?? (facts.Chassis == ChassisKind.Laptop ? MachineProfile.DefaultLaptop : MachineProfile.DefaultDesktop);
        return ((stored ?? ServiceSettings.Default) with { Profile = facts.ToProfile(basis) }, true);
    }

    /// <summary>Whether the profile holds monitor settings from before monitors were detected (Plan J) that haven't been
    /// carried over yet: a count of monitors or the choice to count them, and no choice for any one monitor.</summary>
    public static bool HasMonitorsToCarryOver(MachineProfile profile)
        => profile.Monitors.Count == 0 && (profile.IncludeMonitors || profile.ExternalMonitors > 0);

    /// <summary>
    /// The old monitor settings carried over. Whether the user counted monitors becomes whether a monitor they haven't
    /// chosen for counts, so a monitor attached later is counted or left out as the ones attached now are. Only a figure
    /// typed for the monitors the user counted needs a choice: the monitors detected now are given it, and one attached
    /// later counts at its own figure, as every monitor does when the old figure was the default. The old count and choice
    /// are cleared, so this happens once.
    /// </summary>
    /// <param name="detected">The keys of the external monitors attached.</param>
    public static MachineProfile CarryOverMonitors(MachineProfile profile, IEnumerable<string> detected) => profile with
    {
        Monitors = profile.IncludeMonitors && profile.MonitorWatts != OldDefaultMonitorWatts
            ? [.. detected.Distinct(StringComparer.Ordinal).Select(key => new MonitorChoice { Key = key, Watts = profile.MonitorWatts })]
            : [],
        CountMonitorsByDefault = profile.IncludeMonitors,
        ExternalMonitors = 0,
        IncludeMonitors = false,
    };
}
