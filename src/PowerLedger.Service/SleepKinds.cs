using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace PowerLedger.Service;

/// <summary>One event from the System log, as <see cref="SleepKinds"/> reads it: its data by name.</summary>
internal readonly record struct PowerEvent(string Provider, int Id, DateTimeOffset At, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// The suspends that are no sleep (Plan AC review). A Fast Startup shutdown signs everyone out and then hibernates, and the
/// service is told of it as of any suspend; a hibernate keeps nothing powered. Neither draws what a sleep does, 0.5 to 2 W,
/// and a laptop's battery loses next to nothing in them, which would teach its sleep drain towards nought.
/// <para>What Windows lets the service tell, and when. As the machine goes, only whether anyone is signed in
/// (<see cref="SomeoneSignedIn"/>): a Fast Startup shutdown has signed everyone out first. A PC put to sleep at the sign-in
/// screen looks the same, and its sleep goes unbooked too. Whether this suspend will be a hibernate nothing says. As it
/// wakes, the System event log says whether it hibernated (<see cref="Hibernated"/>): Kernel-Power 42, entering a sleep
/// state, written as it went with TargetState 5 for a hibernate; Kernel-Power 107, resumed, with WakeFromState 5 from one;
/// Kernel-Boot 27 with BootType 1 after a Fast Startup boot and 2 after a resume from hibernation; and the Power
/// Troubleshooter's 1, with the time it went to sleep and its TargetState. A Modern Standby PC that hibernates hours into
/// its sleep logs its 42 then, so the standby before it still counts. Should the log not hold these yet when the wake is
/// handled, or not answer within <see cref="LogWait"/>, the sleep counts as a sleep, as it did before.</para>
/// </summary>
internal static class SleepKinds
{
    /// <summary>The longest the wake waits for the event log.</summary>
    public static readonly TimeSpan LogWait = TimeSpan.FromSeconds(2);

    /// <summary>How far before the suspend an event may be stamped and still be this sleep's: the clock and the log's
    /// stamps are a moment apart.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string KernelBoot = "Microsoft-Windows-Kernel-Boot";
    private const string Troubleshooter = "Microsoft-Windows-Power-Troubleshooter";
    private const string Hibernate = "5";

    /// <summary>Whether anyone is signed in to Windows, at the screen, locked or switched away from; true when Windows won't
    /// say, so a sleep counts as it did before.</summary>
    public static bool SomeoneSignedIn()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count)) return true;
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(sessions + (i * size));
                if (session.SessionId != 0 && session.State is WtsActive or WtsConnected or WtsDisconnected && UserOf(session.SessionId) is { Length: > 0 }) return true;
            }
            return false;
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
    }

    /// <summary>When the machine hibernated, if it did since <paramref name="since"/>, from the System event log; null when
    /// it slept, or the log cannot tell within <see cref="LogWait"/>.</summary>
    public static DateTimeOffset? Hibernated(DateTimeOffset since)
    {
        var read = Task.Run(() => Read(since));
        try
        {
            return read.Wait(LogWait) ? HibernatedAt(read.Result, since) : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>When the machine hibernated since <paramref name="since"/>, from what the log holds: the earliest time an
    /// entry into hibernation was logged with, or the suspend itself when the log says only that it woke from one.</summary>
    internal static DateTimeOffset? HibernatedAt(IEnumerable<PowerEvent> events, DateTimeOffset since)
    {
        DateTimeOffset? entered = null;
        var hibernated = false;
        foreach (var e in events)
        {
            if (e.At < since - Slack) continue;
            switch (e.Provider, e.Id)
            {
                case (KernelPower, 42) when Is(e, "TargetState", Hibernate):
                    entered = Earliest(entered, e.At);
                    break;
                case (KernelPower, 107) when Is(e, "WakeFromState", Hibernate):
                case (KernelBoot, 27) when Is(e, "BootType", "1") || Is(e, "BootType", "2"):
                    hibernated = true;
                    break;
                case (Troubleshooter, 1) when Is(e, "TargetState", Hibernate):
                    entered = Earliest(entered, e.Data.TryGetValue("SleepTime", out var text)
                        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var went) ? went : since);
                    break;
            }
        }
        return entered is { } at ? (at < since ? since : at) : hibernated ? since : null;
    }

    private static bool Is(PowerEvent e, string name, string value) => e.Data.TryGetValue(name, out var found) && found == value;

    private static DateTimeOffset Earliest(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;

    /// <summary>The log's power events, newest first, back to the first one before <paramref name="since"/>: the log is read
    /// backwards so that it is not scanned to its start.</summary>
    private static List<PowerEvent> Read(DateTimeOffset since)
    {
        const string query = $"*[System[Provider[@Name='{KernelPower}' or @Name='{KernelBoot}' or @Name='{Troubleshooter}'] and (EventID=42 or EventID=107 or EventID=27 or EventID=1)]]";
        var events = new List<PowerEvent>();
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query) { ReverseDirection = true });
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                if (record.TimeCreated is not { } created) continue;
                var at = new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero);
                events.Add(new PowerEvent(record.ProviderName, record.Id, at, Data(record.ToXml())));
                if (at < since - Slack) break;
            }
        }
        return events;
    }

    private static Dictionary<string, string> Data(string xml)
    {
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in XDocument.Parse(xml).Descendants(ns + "Data"))
        {
            if (element.Attribute("Name")?.Value is { } name) data[name] = element.Value;
        }
        return data;
    }

    private static string? UserOf(uint session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WtsUserName, out var buffer, out _)) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private const int WtsActive = 0;
    private const int WtsConnected = 1;
    private const int WtsDisconnected = 4;
    private const int WtsUserName = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memory);
}
