using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using PowerLedger.Contracts;

namespace PowerLedger.Service.Households;

/// <summary>
/// Hands the household's notices to the pipe clients in the console session, the one at the screen (households design §9),
/// and to no other: a prompt to join or approve is for the person in front of the PC. Each client has a small queue, so one
/// that stops reading holds up nobody.
/// </summary>
/// <param name="consoleSession">The session at the screen; Windows' own when null.</param>
internal sealed class NoticeHub(Func<uint>? consoleSession = null)
{
    public const int Backlog = 16;

    /// <summary>What WTSGetActiveConsoleSessionId gives while no session is at the screen.</summary>
    public const uint NoSession = 0xFFFFFFFF;

    private readonly Func<uint> _consoleSession = consoleSession ?? ConsoleSessions.Active;
    private readonly Lock _gate = new();
    private Subscriber[] _subscribers = [];

    /// <summary>True while a client in the console session is listening: someone can be asked.</summary>
    public bool AnyoneAtTheScreen
    {
        get
        {
            var console = _consoleSession();
            return console != NoSession && Volatile.Read(ref _subscribers).Any(subscriber => subscriber.Session == console);
        }
    }

    /// <summary>A client in <paramref name="session"/> starts listening.</summary>
    public ChannelReader<HouseholdNotice> Subscribe(uint session)
    {
        var channel = Channel.CreateBounded<HouseholdNotice>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate) _subscribers = [.. _subscribers, new Subscriber(channel, session)];
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<HouseholdNotice> reader)
    {
        lock (_gate)
        {
            var subscriber = _subscribers.FirstOrDefault(s => s.Channel.Reader == reader);
            if (subscriber is null) return;
            subscriber.Channel.Writer.TryComplete();
            _subscribers = [.. _subscribers.Where(s => s != subscriber)];
        }
    }

    /// <summary>Sends the notice to every client in the console session.</summary>
    /// <returns>True when at least one was there to get it.</returns>
    public bool Publish(HouseholdNotice notice)
    {
        var console = _consoleSession();
        if (console == NoSession) return false;
        var sent = false;
        foreach (var subscriber in Volatile.Read(ref _subscribers))
        {
            if (subscriber.Session == console) sent |= subscriber.Channel.Writer.TryWrite(notice);
        }
        return sent;
    }

    private sealed record Subscriber(Channel<HouseholdNotice> Channel, uint Session);
}

/// <summary>Which Windows session a pipe client is in, and which one is at the screen.</summary>
internal static class ConsoleSessions
{
    public static uint Active() => WTSGetActiveConsoleSessionId();

    /// <summary>The session of the client on the other end of a server pipe; null when Windows won't say.</summary>
    public static uint? OfClient(SafePipeHandle pipe) => GetNamedPipeClientSessionId(pipe, out var session) ? session : null;

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);
}
