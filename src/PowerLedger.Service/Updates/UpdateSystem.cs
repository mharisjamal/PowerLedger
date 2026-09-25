using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PowerLedger.Service.Households;

namespace PowerLedger.Service.Updates;

/// <summary>A setup the service started, detached: it outlives the service, which setup stops and replaces.</summary>
internal interface ISetupProcess : IDisposable
{
    /// <summary>Setup's exit code once it ends; reached only when the service is still running then, so the update
    /// didn't replace it.</summary>
    Task<int> ExitAsync(CancellationToken cancel);
}

/// <summary>Windows as the update worker needs it (Plan Q §4): sessions, the App, and setup.</summary>
internal interface IUpdateSystem
{
    /// <summary>The session at the screen, or <see cref="NoticeHub.NoSession"/>.</summary>
    uint ConsoleSession();

    /// <summary>Whether a user is signed in to <paramref name="session"/>.</summary>
    bool UserSignedIn(uint session);

    /// <summary>Asks every running App to exit, as the tray's Exit does, and waits for them; ends any still running after
    /// that, since setup can't replace a program in use. The service opens the App again once setup is done.</summary>
    Task CloseAppsAsync(CancellationToken cancel);

    /// <summary>Starts setup from the held installer with <paramref name="arguments"/>, detached.</summary>
    ISetupProcess StartSetup(VerifiedInstaller installer, string arguments);

    /// <summary>Whether the App is running in <paramref name="session"/>.</summary>
    bool AppRunningIn(uint session);

    /// <summary>Starts the App at <paramref name="path"/> as the user signed in to <paramref name="session"/>, on their
    /// desktop. Throws <see cref="Win32Exception"/> when Windows won't.</summary>
    void LaunchApp(uint session, string path, string arguments);
}

/// <summary>
/// The real thing, for a service running as LocalSystem: WTSQueryUserToken and CreateProcessAsUser to reach the user's
/// session, and the App's own exit event (<c>Local\PowerLedger.App.Exit</c>, which installer\PowerLedger.iss sets too)
/// reached through each session's namespace.
/// </summary>
internal sealed class WindowsUpdateSystem(ILogger log) : IUpdateSystem
{
    /// <summary>How long the Apps get to exit before they are ended.</summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    private const string AppProcess = "PowerLedger";
    private const string ExitEvent = "PowerLedger.App.Exit";
    private const uint EventModifyState = 0x0002;
    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public uint ConsoleSession() => ConsoleSessions.Active();

    public bool UserSignedIn(uint session)
    {
        if (session == NoticeHub.NoSession || !WTSQueryUserToken(session, out var token)) return false;
        token.Dispose();
        return true;
    }

    public async Task CloseAppsAsync(CancellationToken cancel)
    {
        var apps = Apps();
        foreach (var session in apps.Select(app => app.SessionId).Distinct())
        {
            using var exit = OpenEvent(EventModifyState, false, $@"Session\{session}\{ExitEvent}");
            if (!exit.IsInvalid) SetEvent(exit);
        }
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        patience.CancelAfter(ExitWait);
        foreach (var app in apps)
        {
            using (app)
            {
                try
                {
                    await app.WaitForExitAsync(patience.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    log.LogWarning("The App in session {Session} didn't exit for the update; ending it", app.SessionId);
                    try
                    {
                        app.Kill();
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    {
                        // Gone meanwhile, or can't be ended: setup's own check deals with it.
                    }
                }
            }
        }
    }

    public ISetupProcess StartSetup(VerifiedInstaller installer, string arguments)
    {
        var setup = Process.Start(new ProcessStartInfo(installer.Path, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installer.Path)!,
        }) ?? throw new InvalidOperationException("Windows didn't start setup.");
        return new SetupProcess(setup);
    }

    public bool AppRunningIn(uint session)
    {
        var apps = Apps();
        var running = apps.Any(app => (uint)app.SessionId == session);
        foreach (var app in apps) app.Dispose();
        return running;
    }

    public void LaunchApp(uint session, string path, string arguments)
    {
        if (!WTSQueryUserToken(session, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!DuplicateTokenEx(token, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (primary)
            {
                if (!CreateEnvironmentBlock(out var environment, primary, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                    var commandLine = $"\"{path}\" {arguments}";
                    if (!CreateProcessAsUser(primary, path, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, environment,
                            Path.GetDirectoryName(path), ref startup, out var started))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(started.Process);
                    CloseHandle(started.Thread);
                }
                finally
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    /// <summary>Every running App, whichever session it is in.</summary>
    private static Process[] Apps() => Process.GetProcessesByName(AppProcess);

    private sealed class SetupProcess(Process setup) : ISetupProcess
    {
        public async Task<int> ExitAsync(CancellationToken cancel)
        {
            await setup.WaitForExitAsync(cancel).ConfigureAwait(false);
            return setup.ExitCode;
        }

        public void Dispose() => setup.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved3;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);

    [DllImport("userenv.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token, string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup,
        out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWaitHandle OpenEvent(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
