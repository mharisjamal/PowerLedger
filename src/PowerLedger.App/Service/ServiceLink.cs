using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>What became of a change sent to the service: <see cref="Problem"/> is null when it was applied, and otherwise
/// says why not in words the App can show.</summary>
internal sealed record WriteResult(string? Problem)
{
    public static WriteResult Done { get; } = new((string?)null);

    public static WriteResult NotConnected { get; } = new("The service isn't running, so nothing was changed.");

    public static WriteResult NoAnswer { get; } = new("The service didn't answer, so the change may not have been made.");

    public bool Succeeded => Problem is null;
}

/// <summary>What the App needs from the service (spec §8). Events are raised on a background thread; a handler must not
/// throw and must hand its work to the UI thread itself.</summary>
internal interface IServiceLink : IAsyncDisposable
{
    /// <summary>A reading the service pushed, once a sample interval while connected.</summary>
    event Action<ReadingFrame>? FrameReceived;

    /// <summary>True once connected and subscribed; false when the connection is lost.</summary>
    event Action<bool>? ConnectionChanged;

    bool IsConnected { get; }

    void Start();

    /// <summary>Null when not connected, or when the service did not answer in time.</summary>
    Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default);

    /// <summary>Null when not connected, or when the service did not answer in time.</summary>
    Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default);

    /// <summary>Asks the service to use these settings (spec §8), once the server has passed the check.</summary>
    Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default);

    /// <summary>Adds a tariff; <paramref name="effectiveFrom"/> null means from now (spec §8).</summary>
    Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default);

    /// <summary>Forgets the learned baseline (spec §5).</summary>
    Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default);

    /// <summary>Tells the service the brightness and the power state its monitors answered with, and the refresh rate and HDR
    /// state Windows drives each at (Plans J and K), once the server has passed the check. Only for a service whose status
    /// lists monitors: one from before them drops a connection that sends it this.</summary>
    Task<WriteResult> ReportBrightnessAsync(
        IReadOnlyList<MonitorBrightness> monitors, IReadOnlyList<MonitorPowerReading> power, IReadOnlyList<MonitorDisplayReading> displays,
        CancellationToken cancel = default);
}

/// <summary>Seconds since the last keyboard or mouse input in this session.</summary>
internal interface IIdleSource
{
    double IdleSeconds();
}

/// <summary>The App runs in the user's session, so GetLastInputInfo describes the person at the keyboard. The service, in
/// session 0, cannot see that, which is why the App reports it (Plan C's rules for Plan D).</summary>
internal sealed class LastInputIdleSource : IIdleSource
{
    public double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>
/// The App's end of \\.\pipe\PowerLedger.v1. It connects, subscribes, passes frames on, answers requests by id and
/// reports idle time every five seconds. When the service goes away it tries again after 1, 2, 4, 8, 16 and then every
/// 30 seconds (spec §8), and subscribes again once back. Each connection's server is checked when it connects, and a
/// change is sent only to one that passed.
/// </summary>
internal sealed class PipeServiceLink(string pipeName, IIdleSource idle, TimeProvider clock, IServerCheck check) : IServiceLink
{
    public static readonly TimeSpan ActivityEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan[] Backoff = [.. new[] { 1, 2, 4, 8, 16, 30 }.Select(s => TimeSpan.FromSeconds(s))];
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PipeMessage>> _pending = new();
    private long _lastId;
    private volatile MessageChannel? _channel;
    private volatile string? _refusal;
    private Task? _run;

    public event Action<ReadingFrame>? FrameReceived;

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _channel is not null;

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    public async Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default)
        => await SendAsync(new GetStatusRequest(NextId()), cancel).ConfigureAwait(false) is StatusReply reply ? reply.Status : null;

    public async Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default)
        => await SendAsync(new GetSettingsRequest(NextId()), cancel).ConfigureAwait(false) is SettingsReply reply ? reply.Settings : null;

    public Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default)
        => WriteAsync(new SetSettingsRequest(NextId(), settings), cancel);

    public Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default)
        => WriteAsync(new SetTariffRequest(NextId(), pricePerKwh, currency, effectiveFrom), cancel);

    public Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default) => WriteAsync(new ResetCalibrationRequest(NextId()), cancel);

    public Task<WriteResult> ReportBrightnessAsync(
        IReadOnlyList<MonitorBrightness> monitors, IReadOnlyList<MonitorPowerReading> power, IReadOnlyList<MonitorDisplayReading> displays,
        CancellationToken cancel = default)
        => WriteAsync(new ReportBrightnessRequest(NextId(), monitors, power, displays), cancel);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null) await _run.ConfigureAwait(false);
        _stop.Dispose();
    }

    private long NextId() => Interlocked.Increment(ref _lastId);

    private async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await stream.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, stop).ConfigureAwait(false);
                failures = 0;
                await ServeAsync(stream, check.Refusal(stream.SafePipeHandle), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or PipeProtocolException)
            {
                // Not running, busy, or gone: try again after the backoff.
            }

            try
            {
                await Task.Delay(Backoff[Math.Min(failures, Backoff.Length - 1)], clock, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            failures++;
        }
    }

    /// <summary>One connection, from subscribing to its end. <paramref name="refusal"/> is the server check's answer.</summary>
    private async Task ServeAsync(Stream stream, string? refusal, CancellationToken stop)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var channel = new MessageChannel(stream);
        var reading = ReadAsync(channel, connection.Token);
        Task? reporting = null;
        try
        {
            if (await RequestAsync(channel, new SubscribeRequest(NextId()), connection.Token).ConfigureAwait(false) is not OkReply) return;
            _refusal = refusal;
            _channel = channel;
            ConnectionChanged?.Invoke(true);
            reporting = ReportActivityAsync(channel, connection.Token);
            await Task.WhenAny(reading, reporting).ConfigureAwait(false);
        }
        finally
        {
            var connected = _channel is not null;
            _channel = null;
            await connection.CancelAsync().ConfigureAwait(false);
            FailPending();
            await Quietly(reading).ConfigureAwait(false);
            if (reporting is not null) await Quietly(reporting).ConfigureAwait(false);
            if (connected) ConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>Every message the service sends, until the connection ends: frames go out, replies find their request.</summary>
    private async Task ReadAsync(MessageChannel channel, CancellationToken cancel)
    {
        while (await channel.ReadAsync(cancel).ConfigureAwait(false) is { } message)
        {
            switch (message)
            {
                case ReadingFrame frame:
                    FrameReceived?.Invoke(frame);
                    break;
                case OkReply reply:
                    Answer(reply.Id, reply);
                    break;
                case StatusReply reply:
                    Answer(reply.Id, reply);
                    break;
                case SettingsReply reply:
                    Answer(reply.Id, reply);
                    break;
                case ErrorReply { Id: { } id } reply:
                    Answer(id, reply);
                    break;
                default:
                    return;   // an error with no id: the service is closing this connection
            }
        }
    }

    private async Task ReportActivityAsync(MessageChannel channel, CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(ActivityEvery, clock);
        do
        {
            await RequestAsync(channel, new ReportActivityRequest(NextId(), idle.IdleSeconds()), cancel).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false));
    }

    private async Task<PipeMessage?> SendAsync(PipeRequest request, CancellationToken cancel)
    {
        if (_channel is not { } channel) return null;
        try
        {
            return await RequestAsync(channel, request, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            return null;   // whatever broke, the caller gets "no answer"; only its own cancellation goes back to it
        }
    }

    /// <summary>A change: sent only while connected to a server that passed the check, and answered in words.</summary>
    private async Task<WriteResult> WriteAsync(PipeRequest request, CancellationToken cancel)
    {
        if (_channel is null) return WriteResult.NotConnected;
        if (_refusal is { } refusal) return new WriteResult(refusal);
        return await SendAsync(request, cancel).ConfigureAwait(false) switch
        {
            OkReply => WriteResult.Done,
            ErrorReply error => new WriteResult(error.Message),
            _ => WriteResult.NoAnswer,
        };
    }

    private async Task<PipeMessage> RequestAsync(MessageChannel channel, PipeRequest request, CancellationToken cancel)
    {
        var reply = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = reply;
        try
        {
            await channel.WriteAsync(request, cancel).ConfigureAwait(false);
            return await reply.Task.WaitAsync(RequestTimeout, clock, cancel).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private void Answer(long id, PipeMessage reply)
    {
        if (_pending.TryRemove(id, out var waiting)) waiting.TrySetResult(reply);
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting)) waiting.TrySetException(new IOException("The connection to the service closed."));
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException
                                          or PipeProtocolException or TimeoutException)
        {
            // The connection is over; how it ended does not matter here.
        }
    }
}
