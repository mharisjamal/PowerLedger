using System.Threading.Channels;
using PowerLedger.Contracts;

namespace PowerLedger.Service.Sharing;

/// <summary>Something only the sharing worker may do, sent from the pipe. <see cref="Reply"/> completes once the worker has
/// done it, with what to tell the App.</summary>
internal abstract class SharingCommand(long id)
{
    private readonly TaskCompletionSource<SharingReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The pipe request's id, which the reply repeats.</summary>
    public long Id { get; } = id;

    public Task<SharingReply> Reply => _reply.Task;

    internal void Answer(bool ok, string message, string? path = null) => _reply.TrySetResult(new SharingReply(Id, ok, message, path));

    internal void Fail(Exception error) => _reply.TrySetException(error);
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
}

/// <summary>An App crash, kept only while Crash and sensor reports is on.</summary>
internal sealed class ReportCrashCommand(long id, CrashReport crash) : SharingCommand(id)
{
    public CrashReport Crash { get; } = crash;
}

/// <summary>Write what an upload would carry now to a file.</summary>
internal sealed class PreviewCommand(long id) : SharingCommand(id);

/// <summary>Send every complete day waiting now.</summary>
internal sealed class SendNowCommand(long id) : SharingCommand(id);

/// <summary>Ask the server to delete everything sent, and forget everything on success.</summary>
internal sealed class DeleteMyDataCommand(long id) : SharingCommand(id);

/// <summary>The sharing worker's inbox. It holds a few hundred commands at most, so an App gone wrong can't fill the
/// service's memory; one that finds it full is told to try again.</summary>
internal sealed class SharingCommands
{
    public const int Capacity = 256;

    private readonly Channel<SharingCommand> _channel = Channel.CreateBounded<SharingCommand>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private volatile bool _closed;

    public ChannelReader<SharingCommand> Reader => _channel.Reader;

    /// <summary>Queues the command for the worker.</summary>
    /// <returns>False when the inbox is full or the service is stopping; the command has then failed.</returns>
    public bool TryQueue(SharingCommand command)
    {
        if (_channel.Writer.TryWrite(command)) return true;
        command.Fail(new InvalidOperationException(_closed ? "The service is stopping." : "The service is busy. Try again in a moment."));
        return false;
    }

    /// <summary>Closes the inbox and fails whatever is still queued, so no request waits for ever.</summary>
    internal void Close()
    {
        _closed = true;
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var command)) command.Fail(new InvalidOperationException("The service is stopping."));
    }
}
