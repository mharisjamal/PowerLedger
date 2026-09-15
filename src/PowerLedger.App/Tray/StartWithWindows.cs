using Microsoft.Win32;

namespace PowerLedger.App;

/// <summary>The HKCU Run entry that starts the App with Windows, in the tray (spec §9: on by default, set by the installer).</summary>
internal sealed class StartWithWindows(string exePath, string keyPath = StartWithWindows.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PowerLedger";

    public string Command => $"\"{exePath}\" --tray";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
