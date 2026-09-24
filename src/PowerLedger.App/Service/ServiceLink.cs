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

/// <summary>What a sharing request did, in words the App can show (data-sharing design §5): the same shape as
/// <see cref="SharingReply"/> without its pipe id, plus what "not connected" and "no answer" say.</summary>
internal sealed record SharingOutcome(bool Ok, string Message, string? Path = null)
{
    public static SharingOutcome NotConnected { get; } = new(false, "The service isn't running, so nothing was changed.");

    public static SharingOutcome NoAnswer { get; } = new(false, "The service didn't answer, so the change may not have been made.");
}

/// <summary>What a household request did, in words the App can show (households design §2): the same shape as
/// <see cref="HouseholdReply"/> without its pipe id, plus what "not connected" and "no answer" say. For
/// <see cref="IServiceLink.StartCodePairingAsync"/> and a first sign-in that linked a household, <see cref="Code"/>
/// carries the code to show.</summary>
internal sealed record HouseholdOutcome(bool Ok, string Message, string? Code = null)
{
    public static HouseholdOutcome NotConnected { get; } = new(false, "The service isn't running, so nothing was changed.");

    public static HouseholdOutcome NoAnswer { get; } = new(false, "The service didn't answer, so the change may not have been made.");
}

/// <summary>What the App needs from the service (spec §8). Events are raised on a background thread; a handler must not
/// throw and must hand its work to the UI thread itself.</summary>
internal interface IServiceLink : IAsyncDisposable
{
    /// <summary>A reading the service pushed, once a sample interval while connected.</summary>
    event Action<ReadingFrame>? FrameReceived;

    /// <summary>True once connected and subscribed; false when the connection is lost.</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>A household notice the service pushed (households design §9): a join or approve prompt, pairing
    /// progress, or information to show, only for an App in the console session.</summary>
    event Action<HouseholdNotice>? HouseholdNoticeReceived;

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

    /// <summary>Records the user's answer to the consent dialog, or a change in Settings → Privacy (data-sharing design §1).</summary>
    Task<SharingOutcome> SetConsentAsync(Consent consent, CancellationToken cancel = default);

    /// <summary>The App's usage counts since the last report; ignored by the service while Usage is off.</summary>
    Task<WriteResult> ReportUsageAsync(UsageCounts counts, CancellationToken cancel = default);

    /// <summary>An App crash caught on an earlier run; ignored by the service while Crash and sensor reports is off.</summary>
    Task<WriteResult> ReportCrashAsync(CrashReport crash, CancellationToken cancel = default);

    /// <summary>Builds what the next upload would carry into a file and answers with its path.</summary>
    Task<SharingOutcome> PreviewUploadAsync(CancellationToken cancel = default);

    /// <summary>Sends every complete day waiting now, instead of at tonight's minute.</summary>
    Task<SharingOutcome> SendNowAsync(CancellationToken cancel = default);

    /// <summary>Asks the server to delete everything sent from this PC.</summary>
    Task<SharingOutcome> DeleteMyDataAsync(CancellationToken cancel = default);

    /// <summary>PowerLedger PCs found on this network (households design §3); null when not connected or the service did
    /// not answer.</summary>
    Task<IReadOnlyList<FoundPc>?> BrowsePcsAsync(CancellationToken cancel = default);

    /// <summary>Starts adding a PC found on this network. How it goes is pushed as <see cref="HouseholdNoticeReceived"/>
    /// events, its comparison code first.</summary>
    Task<HouseholdOutcome> AddPcAsync(string instanceId, CancellationToken cancel = default);

    /// <summary>Makes a one-time code for adding a PC that isn't on this network (households design §4); the outcome
    /// carries the code.</summary>
    Task<HouseholdOutcome> StartCodePairingAsync(CancellationToken cancel = default);

    /// <summary>Joins a household with the code another PC showed.</summary>
    Task<HouseholdOutcome> JoinByCodeAsync(string code, CancellationToken cancel = default);

    /// <summary>The user's answer to a pushed Join or Approve prompt.</summary>
    Task<HouseholdOutcome> AnswerPromptAsync(string promptId, bool accept, CancellationToken cancel = default);

    /// <summary>Removes another PC from the household; the household key changes (households design §6).</summary>
    Task<HouseholdOutcome> RemovePcAsync(string deviceId, CancellationToken cancel = default);

    Task<HouseholdOutcome> LeaveHouseholdAsync(CancellationToken cancel = default);

    /// <summary>This PC's name in the household, 1 to 40 characters.</summary>
    Task<HouseholdOutcome> RenamePcAsync(string name, CancellationToken cancel = default);

    /// <summary>Whether other PCs on a Private network can find this one.</summary>
    Task<HouseholdOutcome> SetDiscoverableAsync(bool on, CancellationToken cancel = default);

    /// <summary>N2: sign in with an ID token the App got from the provider in the browser, and the nonce salt
    /// (households design §7); with a recovery code, restores a household without approval.</summary>
    Task<HouseholdOutcome> SignInAsync(string provider, string idToken, string nonce, string? recoveryCode, CancellationToken cancel = default);

    Task<HouseholdOutcome> SignOutAsync(CancellationToken cancel = default);

    Task<HouseholdOutcome> DeleteAccountAsync(CancellationToken cancel = default);
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
    private readonly SemaphoreSlim _sharingGate = new(1, 1);   // never disposed: a request let go at shutdown still releases it
    private volatile bool _disposed;
    private int _disposing;
    private long _lastId;
    private volatile MessageChannel? _channel;
    private volatile string? _refusal;
    private Task? _run;

    public event Action<ReadingFrame>? FrameReceived;

    public event Action<bool>? ConnectionChanged;

    public event Action<HouseholdNotice>? HouseholdNoticeReceived;

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

    public Task<SharingOutcome> SetConsentAsync(Consent consent, CancellationToken cancel = default)
        => SharingAsync(new SetConsentRequest(NextId(), consent), cancel);

    public Task<WriteResult> ReportUsageAsync(UsageCounts counts, CancellationToken cancel = default)
        => WriteAsync(new ReportUsageRequest(NextId(), counts), cancel);

    public Task<WriteResult> ReportCrashAsync(CrashReport crash, CancellationToken cancel = default)
        => WriteAsync(new ReportCrashRequest(NextId(), crash), cancel);

    public Task<SharingOutcome> PreviewUploadAsync(CancellationToken cancel = default)
        => SharingAsync(new PreviewUploadRequest(NextId()), cancel);

    public Task<SharingOutcome> SendNowAsync(CancellationToken cancel = default)
        => SharingAsync(new SendNowRequest(NextId()), cancel);

    public Task<SharingOutcome> DeleteMyDataAsync(CancellationToken cancel = default)
        => SharingAsync(new DeleteMyDataRequest(NextId()), cancel);

    public async Task<IReadOnlyList<FoundPc>?> BrowsePcsAsync(CancellationToken cancel = default)
        => await SendAsync(new BrowsePcsRequest(NextId()), cancel).ConfigureAwait(false) is FoundPcsReply reply ? reply.Pcs : null;

    public Task<HouseholdOutcome> AddPcAsync(string instanceId, CancellationToken cancel = default)
        => HouseholdAsync(new AddPcRequest(NextId(), instanceId), cancel);

    public Task<HouseholdOutcome> StartCodePairingAsync(CancellationToken cancel = default)
        => HouseholdAsync(new StartCodePairingRequest(NextId()), cancel);

    public Task<HouseholdOutcome> JoinByCodeAsync(string code, CancellationToken cancel = default)
        => HouseholdAsync(new JoinByCodeRequest(NextId(), code), cancel);

    public Task<HouseholdOutcome> AnswerPromptAsync(string promptId, bool accept, CancellationToken cancel = default)
        => HouseholdAsync(new AnswerPromptRequest(NextId(), promptId, accept), cancel);

    public Task<HouseholdOutcome> RemovePcAsync(string deviceId, CancellationToken cancel = default)
        => HouseholdAsync(new RemovePcRequest(NextId(), deviceId), cancel);

    public Task<HouseholdOutcome> LeaveHouseholdAsync(CancellationToken cancel = default)
        => HouseholdAsync(new LeaveHouseholdRequest(NextId()), cancel);

    public Task<HouseholdOutcome> RenamePcAsync(string name, CancellationToken cancel = default)
        => HouseholdAsync(new RenamePcRequest(NextId(), name), cancel);

    public Task<HouseholdOutcome> SetDiscoverableAsync(bool on, CancellationToken cancel = default)
        => HouseholdAsync(new SetDiscoverableRequest(NextId(), on), cancel);

    public Task<HouseholdOutcome> SignInAsync(string provider, string idToken, string nonce, string? recoveryCode, CancellationToken cancel = default)
        => HouseholdAsync(new SignInRequest(NextId(), provider, idToken, nonce, recoveryCode), cancel);

    public Task<HouseholdOutcome> SignOutAsync(CancellationToken cancel = default) => HouseholdAsync(new SignOutRequest(NextId()), cancel);

    public Task<HouseholdOutcome> DeleteAccountAsync(CancellationToken cancel = default) => HouseholdAsync(new DeleteAccountRequest(NextId()), cancel);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1) return;                  // once, however often it is asked
        _disposed = true;
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
                case SharingReply reply:
                    Answer(reply.Id, reply);
                    break;
                case FoundPcsReply reply:
                    Answer(reply.Id, reply);
                    break;
                case HouseholdReply reply:
                    Answer(reply.Id, reply);
                    break;
                case HouseholdNotice notice:
                    // Pushed, not a reply to anything pending: households design §9.
                    HouseholdNoticeReceived?.Invoke(notice);
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

    /// <summary>A sharing request: sent only while connected to a server that passed the check, and answered in words
    /// (data-sharing design §5). The service serves one request at a time per connection, and its own reply limits start
    /// when it queues a request rather than when the App wrote it, so at most one of these is ever outstanding: a second
    /// one waits here, before it is written, for the first to answer — otherwise a request could be carried out after the
    /// App had already given up on it (e.g. Delete sent right after a modeless Send now's window).</summary>
    private async Task<SharingOutcome> SharingAsync(PipeRequest request, CancellationToken cancel)
    {
        if (_channel is null) return SharingOutcome.NotConnected;
        if (_refusal is { } refusal) return new SharingOutcome(false, refusal);
        await _sharingGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (_disposed) return SharingOutcome.NotConnected;                  // its turn came after the App began to exit
            return await SendAsync(request, cancel).ConfigureAwait(false) switch
            {
                SharingReply reply => new SharingOutcome(reply.Ok, reply.Message, reply.Path),
                ErrorReply error => new SharingOutcome(false, error.Message),
                _ => SharingOutcome.NoAnswer,
            };
        }
        finally
        {
            _sharingGate.Release();
        }
    }

    /// <summary>A household request (households design §2): sent only while connected to a server that passed the check,
    /// and answered in words. Unlike a sharing request, these are not serialised against one another: the service
    /// answers each within 8 s (households design §9), and the App may have more than one outstanding, such as browsing
    /// while a pairing is under way.</summary>
    private async Task<HouseholdOutcome> HouseholdAsync(PipeRequest request, CancellationToken cancel)
    {
        if (_channel is null) return HouseholdOutcome.NotConnected;
        if (_refusal is { } refusal) return new HouseholdOutcome(false, refusal);
        return await SendAsync(request, cancel).ConfigureAwait(false) switch
        {
            HouseholdReply reply => new HouseholdOutcome(reply.Ok, reply.Message, reply.Code),
            ErrorReply error => new HouseholdOutcome(false, error.Message),
            _ => HouseholdOutcome.NoAnswer,
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
