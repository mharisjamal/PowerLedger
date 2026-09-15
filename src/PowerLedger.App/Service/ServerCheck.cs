using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.App;

/// <summary>Whether the process at the other end of the pipe may be sent changes (Plan C's rule, spec §11).</summary>
internal interface IServerCheck
{
    /// <summary>Null when the server may be sent changes; otherwise why not, in words the App can show.</summary>
    string? Refusal(SafePipeHandle pipe);
}

/// <summary>A development run, started with --pipe, names its own pipe and trusts whatever serves it.</summary>
internal sealed class TrustAnyServer : IServerCheck
{
    public string? Refusal(SafePipeHandle pipe) => null;
}

/// <summary>
/// The process serving the pipe must be the one Windows runs for the PowerLedger service. Anyone may create a pipe with
/// PowerLedger's name before the service does, so the name proves nothing. The Service Control Manager tells any
/// signed-in user which process runs a service, where the service's own process, running as LocalSystem, won't tell an
/// unelevated App even its path.
/// </summary>
/// <param name="serviceProcess">The installed service's process id: 0 while it isn't running, null when it isn't installed.</param>
internal sealed class InstalledServiceCheck(Func<uint?> serviceProcess) : IServerCheck
{
    public const string ServiceName = "PowerLedger";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;

    public static InstalledServiceCheck FromServiceManager() => new(() => ServiceProcess(ServiceName));

    public string? Refusal(SafePipeHandle pipe)
    {
        if (serviceProcess() is not { } running) return "The PowerLedger service isn't installed, so nothing can be changed.";
        if (ServerProcess(pipe) is not { } server) return "The program serving PowerLedger's pipe couldn't be identified, so nothing was sent.";
        return running != 0 && server == running
            ? null
            : "The program serving PowerLedger's pipe isn't the installed service, so nothing was sent.";
    }

    /// <summary>The process serving a pipe, or null when Windows won't say.</summary>
    internal static uint? ServerProcess(SafePipeHandle pipe) => GetNamedPipeServerProcessId(pipe, out var id) ? id : null;

    /// <summary>The process Windows runs for a service: 0 while it has none, null when it isn't installed or the Service
    /// Control Manager won't say. The link asks on every connection, so nothing may escape from here: each failure is
    /// an answer from Windows, never an exception.</summary>
    internal static uint? ServiceProcess(string name)
    {
        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;
        try
        {
            manager = OpenSCManagerW(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return null;
            service = OpenServiceW(manager, name, ServiceQueryStatus);
            if (service == IntPtr.Zero) return null;
            return QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _)
                ? status.ProcessId
                : null;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>SERVICE_STATUS_PROCESS: nine DWORDs, of which only the process id is read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
