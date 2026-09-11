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
/// changes, which means a different machine. Otherwise the stored profile, with the user's corrections, stands.
/// </summary>
internal static class ProfilePolicy
{
    /// <returns>The settings to use, and whether they differ from what was stored.</returns>
    public static (ServiceSettings Settings, bool Changed) Apply(ServiceSettings? stored, string? storedHash, InventoryFacts facts)
    {
        if (stored is not null && storedHash == facts.Hash) return (stored, false);
        var basis = stored?.Profile ?? (facts.Chassis == ChassisKind.Laptop ? MachineProfile.DefaultLaptop : MachineProfile.DefaultDesktop);
        return ((stored ?? ServiceSettings.Default) with { Profile = facts.ToProfile(basis) }, true);
    }
}
