using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>
/// A stand-in for the service's end of the pipe: it answers subscribe, status, settings and activity reports, pushes
/// frames on demand, and can stop and start again like a restarting service.
/// </summary>
internal sealed class FakeService(string name) : IAsyncDisposable
{
    private CancellationTokenSource _stop = new();
    private Task _serving = Task.CompletedTask;
    private volatile MessageChannel? _client;

    public ConcurrentQueue<PipeRequest> Requests { get; } = new();

    public ServiceStatus Status { get; set; } = Statuses.Running();

    /// <summary>When set, the service refuses every change with this message, as it does a value out of range.</summary>
    public string? Refuse { get; set; }

    /// <summary>The path a preview upload answers with.</summary>
    public string PreviewPath { get; set; } = @"C:\ProgramData\PowerLedger\Sent\preview.json";

    public bool HasClient => _client is not null;

    /// <summary>When set, the reply to a request this matches waits for <see cref="ReleaseHeldReplies"/>, so a test can
    /// see whether the App writes a further request before an earlier one has been answered (finding 6: the service
    /// serves one request at a time, so the App must not have two of the slow sharing requests outstanding together).
    /// Requests are still read and enqueued into <see cref="Requests"/> as soon as they arrive, whether or not their
    /// reply is held, since that is what shows whether the App wrote them.</summary>
    public Func<PipeRequest, bool>? HoldReplyTo { get; set; }

    private readonly TaskCompletionSource _releaseHeld = new();

    /// <summary>Lets every reply <see cref="HoldReplyTo"/> is holding go out.</summary>
    public void ReleaseHeldReplies() => _releaseHeld.TrySetResult();

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _serving = ServeAsync(_stop.Token);
    }

    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task PushAsync(PipeMessage message)
    {
        await WaitFor.True(() => HasClient);
        await _client!.WriteAsync(message);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task ServeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(stop);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }

            var channel = new MessageChannel(server);
            await using (channel)
            {
                _client = channel;
                try
                {
                    while (await channel.ReadAsync(stop) is PipeRequest request)
                    {
                        Requests.Enqueue(request);
                        _ = ReplyAsync(channel, request, stop);   // replying never blocks reading the next request
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client left, or the test stopped the service.
                }
                finally
                {
                    _client = null;
                }
            }
        }
    }

    private async Task ReplyAsync(MessageChannel channel, PipeRequest request, CancellationToken stop)
    {
        try
        {
            if (HoldReplyTo?.Invoke(request) == true) await _releaseHeld.Task.WaitAsync(stop).ConfigureAwait(false);
            await channel.WriteAsync(Reply(request), stop).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client left, or the test stopped the service, while this reply was held or on its way.
        }
    }

    private PipeMessage Reply(PipeRequest request) => request switch
    {
        GetStatusRequest r => new StatusReply(r.Id, Status),
        GetSettingsRequest r => new SettingsReply(r.Id, ServiceSettings.Default),
        SetSettingsRequest or SetTariffRequest or ResetCalibrationRequest or SetConsentRequest or PreviewUploadRequest or SendNowRequest
            or DeleteMyDataRequest or ReportUsageRequest or ReportCrashRequest when Refuse is { } refusal => new ErrorReply(request.Id, refusal),
        SetConsentRequest r => new SharingReply(r.Id, true, "Saved."),
        PreviewUploadRequest r => new SharingReply(r.Id, true, "Written.", PreviewPath),
        SendNowRequest r => new SharingReply(r.Id, true, "Sent."),
        DeleteMyDataRequest r => new SharingReply(r.Id, true, "Your data has been deleted from the server."),
        _ => new OkReply(request.Id),
    };
}
