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

    public bool HasClient => _client is not null;

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
                        await channel.WriteAsync(Reply(request), stop);
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

    private PipeMessage Reply(PipeRequest request) => request switch
    {
        GetStatusRequest r => new StatusReply(r.Id, Status),
        GetSettingsRequest r => new SettingsReply(r.Id, ServiceSettings.Default),
        SetSettingsRequest or SetTariffRequest or ResetCalibrationRequest when Refuse is { } refusal => new ErrorReply(request.Id, refusal),
        _ => new OkReply(request.Id),
    };
}
