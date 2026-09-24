using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>
/// The service's end of \\.\pipe\PowerLedger.v1 (spec §8, §11). Network logons are denied; local signed-in users may
/// read and write but not create instances, so no other process can serve the name while the service runs. One
/// listening instance always waits for the next client, and each client is served on its own task, so a slow or
/// broken client holds up nobody else. A client that subscribes gets each reading, and, when it is in the console session,
/// the household's notices (households design §9).
/// </summary>
internal sealed class PipeServer(
    PipeHandler handler, LiveFeed feed, ServiceSignals signals, ILogger<PipeServer> log, string pipeName, Households.NoticeHub? notices = null)
    : BackgroundService
{
    /// <summary>More clients than this wait for a free instance.</summary>
    public const int MaxClients = 16;

    private readonly List<Task> _clients = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the first instance of the pipe exists, so a client can connect; fails if another process already
    /// serves the name. .NET runs ExecuteAsync on the thread pool, so StartAsync returns before this.
    /// </summary>
    internal Task Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var first = true;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = Create(pipeName, first);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (first)
                    {
                        log.LogCritical(error, "Another process already serves the pipe {Pipe}, so the App cannot reach this service", pipeName);
                        _listening.TrySetException(error);
                        throw;
                    }
                    log.LogWarning(error, "No free pipe instance; trying again in a second");
                    await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                    continue;
                }

                first = false;
                _listening.TrySetResult();
                try
                {
                    await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (IOException error)
                {
                    log.LogDebug(error, "A client left before it was served");
                    await server.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                Track(ServeAsync(server, stop));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            _listening.TrySetCanceled();
            Task[] running;
            lock (_gate) running = [.. _clients];
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    /// <summary>One instance of the pipe. The first must be the first anywhere, which fails if another process already serves the name.</summary>
    internal static NamedPipeServerStream Create(string name, bool first) => NamedPipeServerStreamAcl.Create(
        name, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        0, 0, Security(), HandleInheritability.None, 0);

    /// <summary>
    /// Network logons denied; SYSTEM, administrators and the account running the server in full; every other local
    /// signed-in user may read and write. The running account's own entry lets a console run add instances; under the
    /// service control manager it is SYSTEM again.
    /// </summary>
    internal static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } self) security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private void Track(Task client)
    {
        lock (_gate) _clients.Add(client);
        _ = client.ContinueWith(
            done =>
            {
                lock (_gate) _clients.Remove(done);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken stop)
    {
        var client = Guid.NewGuid().ToString("N");
        var session = Households.ConsoleSessions.OfClient(stream.SafePipeHandle);
        using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var channel = new MessageChannel(stream);
        ChannelReader<ReadingFrame>? frames = null;
        ChannelReader<HouseholdNotice>? householdNotices = null;
        Task? pump = null;
        try
        {
            while (await channel.ReadAsync(done.Token).ConfigureAwait(false) is { } message)
            {
                var reply = await handler.HandleAsync(message, client, done.Token, session).ConfigureAwait(false);
                await channel.WriteAsync(Sendable(message, reply), done.Token).ConfigureAwait(false);
                if (message is SubscribeRequest && frames is null)
                {
                    frames = feed.Subscribe();
                    if (notices is not null && session is { } at) householdNotices = notices.Subscribe(at);
                    pump = Task.WhenAll(PumpAsync(channel, frames, done.Token), PumpAsync(channel, householdNotices, done.Token));
                }
            }
        }
        catch (PipeProtocolException error)
        {
            log.LogWarning("A client broke the protocol and was disconnected: {Reason}", error.Message);
            await TryWriteAsync(channel, new ErrorReply(null, error.Message), done.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, or the service is stopping.
        }
        catch (Exception error)
        {
            log.LogError(error, "Serving a pipe client failed");
        }
        finally
        {
            await done.CancelAsync().ConfigureAwait(false);
            if (frames is not null) feed.Unsubscribe(frames);
            if (householdNotices is not null) notices!.Unsubscribe(householdNotices);
            signals.ForgetClient(client);
            if (pump is not null) await pump.ConfigureAwait(false);
        }
    }

    /// <summary>The reply, or an <see cref="ErrorReply"/> in its place when it would be over the pipe's 64 KB (plan 0.9): the
    /// client hears why, and the connection stays.</summary>
    private PipeMessage Sendable(PipeMessage request, PipeMessage reply)
    {
        try
        {
            PipeProtocol.Serialize(reply);
            return reply;
        }
        catch (PipeProtocolException error)
        {
            log.LogWarning("A reply was too large to send: {Reason}", error.Message);
            return new ErrorReply((request as PipeRequest)?.Id, "The answer was too large to send.");
        }
    }

    /// <summary>Writes what the reader gives until it ends or the connection does; nothing when there is no reader.</summary>
    private static async Task PumpAsync<T>(MessageChannel channel, ChannelReader<T>? messages, CancellationToken cancel) where T : PipeMessage
    {
        if (messages is null) return;
        try
        {
            await foreach (var message in messages.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                await channel.WriteAsync(message, cancel).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, or the connection is closing.
        }
    }

    private static async Task TryWriteAsync(MessageChannel channel, PipeMessage message, CancellationToken cancel)
    {
        try
        {
            await channel.WriteAsync(message, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Nobody left to tell.
        }
    }
}
