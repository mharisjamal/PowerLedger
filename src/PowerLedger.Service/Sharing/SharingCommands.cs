using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using PowerLedger.Contracts;

namespace PowerLedger.Service.Sharing;

/// <summary>
/// Something only the sharing worker may do, sent from the pipe. <see cref="Reply"/> completes once the worker has done it,
/// with what to tell the App. It never faults: a command the worker couldn't take is answered no, since nobody may be
/// waiting on it, and a fault nobody saw would be caught as a crash. The worker takes a command before doing any of it,
/// and the pipe gives up on one it has waited on too long only if the worker hasn't: so a request the App was told got no
/// answer in time is never done.
/// </summary>
internal abstract class SharingCommand(long id)
{
    private const int Waiting = 0, Taken = 1, GivenUp = 2;
    private readonly TaskCompletionSource<SharingReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    /// <summary>The pipe request's id, which the reply repeats.</summary>
    public long Id { get; } = id;

    public Task<SharingReply> Reply => _reply.Task;

    /// <summary>True when the App waits on the reply; false for its usage counts and crashes, which the pipe acknowledges
    /// once they are queued.</summary>
    public virtual bool AppWaits => true;

    /// <summary>When it was queued: the App has its answer within <see cref="SharingWorker.AppWait"/> of then, whatever came
    /// before it. Null for one handed to the worker directly.</summary>
    public DateTimeOffset? QueuedAt { get; private set; }

    internal void Queued(DateTimeOffset at) => QueuedAt = at;

    /// <summary>The worker takes the command before doing any of it.</summary>
    /// <returns>False when the pipe has given up on it: it is then left undone.</returns>
    internal bool TryTake() => Interlocked.CompareExchange(ref _state, Taken, Waiting) == Waiting;

    /// <summary>The pipe gives up on the command, answering it with <paramref name="message"/>.</summary>
    /// <returns>True when the worker hadn't taken it, and now never will; false when it has, and its answer is on its way.</returns>
    internal bool TryGiveUp(string message)
    {
        if (Interlocked.CompareExchange(ref _state, GivenUp, Waiting) != Waiting) return false;
        Answer(false, message);
        return true;
    }

    /// <summary>Answers once; later answers are ignored.</summary>
    internal void Answer(bool ok, string message, string? path = null) => _reply.TrySetResult(new SharingReply(Id, ok, message, path));
}

/// <summary>The user's answer, from the dialog or Settings → Privacy.</summary>
internal sealed class SetConsentCommand(long id, Consent consent) : SharingCommand(id)
{
    public Consent Consent { get; } = consent;
}

/// <summary>The App's counts since its last report, kept only while Usage is on.</summary>
internal sealed class ReportUsageCommand(long id, UsageCounts counts) : SharingCommand(id)
{
    public UsageCounts Counts { get; } = counts;

    public override bool AppWaits => false;
}

/// <summary>An App crash, kept only while Crash and sensor reports is on.</summary>
internal sealed class ReportCrashCommand(long id, CrashReport crash) : SharingCommand(id)
{
    public CrashReport Crash { get; } = crash;

    public override bool AppWaits => false;
}

/// <summary>Write what an upload would carry now to a file.</summary>
internal sealed class PreviewCommand(long id) : SharingCommand(id);

/// <summary>Send every complete day waiting now.</summary>
internal sealed class SendNowCommand(long id) : SharingCommand(id);

/// <summary>Ask the server to delete everything sent, and forget everything on success.</summary>
internal sealed class DeleteMyDataCommand(long id) : SharingCommand(id);

/// <summary>
/// The sharing worker's inbox. It holds a few hundred commands at most, so an App gone wrong can't fill the service's
/// memory; one that finds it full is told to try again. It also tells the worker when a request the App waits on is
/// queued, so an upload or anything else the worker does on its own gives way to it (see <see cref="Attention"/>).
/// </summary>
/// <param name="clock">Stamps each command as it is queued; the system's clock when null.</param>
internal sealed class SharingCommands(TimeProvider? clock = null)
{
    public const int Capacity = 256;

    internal const string Stopping = "The service is stopping.";
    internal const string Busy = "The service is busy. Try again in a moment.";

    private readonly Channel<SharingCommand> _channel = Channel.CreateBounded<SharingCommand>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private CancellationTokenSource _attention = new();
    private int _appWaiting;
    private volatile bool _closed;

    public ChannelReader<SharingCommand> Reader => _channel.Reader;

    /// <summary>True while a request the App waits on is queued.</summary>
    public bool AppWaiting
    {
        get
        {
            lock (_gate) return _appWaiting > 0;
        }
    }

    /// <summary>Cancelled as soon as a request the App waits on is queued, for the worker's own requests to the server to
    /// give way to it; one not cancelled while none is.</summary>
    public CancellationToken Attention
    {
        get
        {
            lock (_gate)
            {
                if (_appWaiting <= 0 && _attention.IsCancellationRequested) _attention = new CancellationTokenSource();
                return _attention.Token;
            }
        }
    }

    /// <summary>Queues the command for the worker, stamped with the time.</summary>
    /// <returns>False when the inbox is full or the service is stopping; the command has then been answered no.</returns>
    public bool TryQueue(SharingCommand command)
    {
        command.Queued(_clock.GetUtcNow());
        if (!_channel.Writer.TryWrite(command))
        {
            command.Answer(false, _closed ? Stopping : Busy);
            return false;
        }
        if (command.AppWaits)
        {
            CancellationTokenSource attention;
            lock (_gate)
            {
                _appWaiting++;
                attention = _attention;
            }
            attention.Cancel();
        }
        return true;
    }

    /// <summary>The next command queued, for the worker.</summary>
    public bool TryTake([MaybeNullWhen(false)] out SharingCommand command)
    {
        if (!_channel.Reader.TryRead(out command)) return false;
        if (command.AppWaits)
        {
            lock (_gate) _appWaiting--;
        }
        return true;
    }

    /// <summary>Closes the inbox and answers whatever is still queued, so no request waits for ever.</summary>
    internal void Close()
    {
        _closed = true;
        _channel.Writer.TryComplete();
        while (TryTake(out var command)) command.Answer(false, Stopping);
    }
}
