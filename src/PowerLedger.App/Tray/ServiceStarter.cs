using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PowerLedger.App;

/// <summary>"Start service" on the Now screen's banner (spec §9). Starting a service needs an administrator, so Windows
/// asks through UAC; a user who declines has changed nothing, and nothing more is said.</summary>
internal static class ServiceStarter
{
    public static void Start()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start PowerLedger")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            // The UAC prompt was declined.
        }
    }
}
