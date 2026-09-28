using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PowerLedger.App;

/// <summary>
/// The service pulse's Restart the service (Aero look design §1). Restarting a service needs an administrator, so Windows
/// asks through UAC, as <see cref="ServiceStarter"/> does for Start; a user who declines has changed nothing. Restart-Service
/// waits for the stop before it starts again, which sc.exe's stop and start one after the other would not. A test puts
/// its own action in <see cref="Run"/>.
/// </summary>
internal static class ServiceRestart
{
    public static Action Run { get; set; } = Restart;

    private static void Restart()
    {
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Process.Start(new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -WindowStyle Hidden -Command Restart-Service -Name PowerLedger")
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
