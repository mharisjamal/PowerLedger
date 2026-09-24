using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>What the household asks of the machine and the network, so tests can supply their own.</summary>
/// <param name="ListenAddress">Where the LAN listener listens: every address for the service, loopback for tests.</param>
/// <param name="MachineName">This PC's Windows name; null for the real one.</param>
/// <param name="RunLoop">False for tests that drive <see cref="HouseholdWorker.RunOnceAsync"/> themselves.</param>
/// <param name="CodeWait">How a pairing by code waits between looks at a slot; null for the clock's own.</param>
internal sealed record HouseholdEnvironment(
    IDiscovery Discovery, INetworkCategory Network, RelayClient Relay, IPAddress ListenAddress, Func<string>? MachineName = null,
    bool RunLoop = true, TimeSpan? BrowseTime = null, PairingTimeouts? Timeouts = null, Func<TimeSpan, CancellationToken, Task>? CodeWait = null);

/// <summary>The household's requests from the pipe (plan 0.2).</summary>
internal interface IHouseholdRequests
{
    /// <summary>Answers within <see cref="HouseholdWorker.AppWait"/>: work that takes longer, a pairing say, goes on after the
    /// answer and reports through pushed notices.</summary>
    /// <param name="session">The Windows session of the pipe client that asked; null when Windows wouldn't say.</param>
    Task<PipeMessage> HandleAsync(PipeRequest request, uint? session, CancellationToken cancel);
}

/// <summary>
/// The household in the service (households design §9): it holds this PC's keys and the household's, announces this PC on
/// Private networks and listens for the others, builds this PC's hour rows every hour, syncs with the members it finds on
/// the network and through the server every 15 minutes, and carries out the App's requests. Pairings run on their own and
/// tell the App how they go in pushed notices; everything that changes the household goes through one gate, which the
/// worker's own work gives way to at once, so every request is answered within <see cref="AppWait"/>.
/// </summary>
internal sealed partial class HouseholdWorker : BackgroundService, IHouseholdRequests
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    /// <summary>The longest the pipe waits for a household request's answer.</summary>
    public static readonly TimeSpan AppWait = TimeSpan.FromSeconds(8);

    /// <summary>How long a request waits for the gate before saying the household is busy: well inside <see cref="AppWait"/>.</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a pairing under way waits for the gate to record its outcome: nobody waits on its answer.</summary>
    private static readonly TimeSpan PairingGateWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultBrowseTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest syncing on the network takes in a turn.</summary>
    internal static readonly TimeSpan LanBudget = TimeSpan.FromMinutes(2);

    /// <summary>How often the network's category is looked at again (plan 0.9): Windows says nothing when only a network's
    /// category changes, so the listener and the announcement stop within this long of the network turning Public.</summary>
    internal static readonly TimeSpan NetworkEvery = TimeSpan.FromMinutes(1);

    /// <summary>The most members found on the network synced with in a turn: twice the most a household has.</summary>
    internal const int MaxFound = 2 * Wire.MaxMembers;

    /// <summary>At most this many PCs found on the network are kept to add, the most recently seen (plan 0.9), so what browsing
    /// answers stays well inside the pipe's 64 KB.</summary>
    internal const int MaxFoundKept = 64;

    /// <summary>A PC found on the network and not seen again for this long is forgotten.</summary>
    internal static readonly TimeSpan FoundFor = TimeSpan.FromMinutes(10);

    /// <summary>A member list older than this is read again before a sync on the network (plan 0.10).</summary>
    internal static readonly TimeSpan MembersFreshFor = TimeSpan.FromMinutes(15);

    /// <summary>How long a sync that came in waits for the member list to be read again before it goes on without.</summary>
    private static readonly TimeSpan FreshenWait = TimeSpan.FromSeconds(5);

    internal const string Busy = "The household is busy. Try again in a moment.";
    internal const string NotInOne = "This PC isn't in a household.";
    internal const string NotAtTheScreen = "Only someone at this PC's screen can change its household.";

    private readonly StatusBoard _board;
    private readonly NoticeHub _notices;
    private readonly HouseholdEnvironment _environment;
    private readonly TimeProvider _clock;
    private readonly ILogger<HouseholdWorker> _log;
    private readonly HouseholdStore _store;
    private readonly HouseholdRepository _household;
    private readonly MemberBook _members;
    private readonly HourRows _rows;
    private readonly LanSync _lanSync;
    private readonly RelaySync _relaySync;
    private readonly CodePairing _codePairing;
    private readonly HouseholdPrompts _prompts;
    private readonly PairingGate _pairingGate;
    private readonly StrangerGate _strangers;
    private readonly HouseholdGate _gate = new();
    private readonly Announcer _announcer;
    private readonly DeviceKeys _keys;
    private readonly PairingTimeouts _timeouts;
    private readonly ConcurrentDictionary<string, (FoundService Service, DateTimeOffset Seen)> _found = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _kick = new(0);
    private readonly Lock _listening = new();
    private LanListener? _listener;
    private ITimer? _networkCheck;
    private long _rowsBuiltForHour = -1;

    public HouseholdWorker(
        SqliteDatabase database, StatusBoard board, NoticeHub notices, HouseholdEnvironment environment, TimeProvider clock,
        ILogger<HouseholdWorker> log)
    {
        _board = board;
        _notices = notices;
        _environment = environment;
        _clock = clock;
        _log = log;
        _store = new HouseholdStore(new SettingsRepository(database), environment.MachineName);
        _household = new HouseholdRepository(database);
        _members = new MemberBook(_store, _household);
        _rows = new HourRows(new AggregateRepository(database), new TariffRepository(database), _household);
        _timeouts = environment.Timeouts ?? PairingTimeouts.Default;
        _lanSync = new LanSync(_household, _members, clock, _timeouts);
        _relaySync = new RelaySync(_store, _household, environment.Relay, clock, log);
        _codePairing = new CodePairing(environment.Relay, clock, environment.CodeWait);
        _prompts = new HouseholdPrompts(notices, clock);
        _pairingGate = new PairingGate(clock);
        _strangers = new StrangerGate(clock);
        _announcer = new Announcer(environment.Discovery, environment.Network, log);
        _keys = _store.DeviceKeys();
    }

    /// <summary>This PC's device ID.</summary>
    public string DeviceId => _keys.DeviceId;

    /// <summary>The instance name this PC is announced under.</summary>
    public string InstanceId => _store.InstanceId;

    /// <summary>The listener's port, once started.</summary>
    public int Port => _listener?.Port ?? 0;

    /// <summary>Pairings and other work still going on after their request was answered.</summary>
    internal Task Running => Task.WhenAll(_running.Keys);

    internal HouseholdStore Store => _store;

    internal HouseholdPrompts Prompts => _prompts;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _environment.Network.Changed += Announce;
        Announce();
        _networkCheck = _clock.CreateTimer(_ => Announce(), null, NetworkEvery, NetworkEvery);
        Publish();
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _environment.Network.Changed -= Announce;
            if (_networkCheck is not null) await _networkCheck.DisposeAsync().ConfigureAwait(false);
            _announcer.Dispose();
            LanListener? listener;
            lock (_listening) (listener, _listener) = (_listener, null);
            if (listener is not null) await listener.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
            }
        }
    }

    public override void Dispose()
    {
        _keys.Dispose();
        _stopping.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_environment.RunLoop) return;
        using var timer = new PeriodicTimer(Every, _clock);
        var tick = timer.WaitForNextTickAsync(stop).AsTask();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _clock, stop).ConfigureAwait(false);   // the service starts first
            while (!stop.IsCancellationRequested)
            {
                await RunOnceSafelyAsync(stop).ConfigureAwait(false);
                var kicked = _kick.WaitAsync(stop);
                if (await Task.WhenAny(tick, kicked).ConfigureAwait(false) == tick)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    tick = timer.WaitForNextTickAsync(stop).AsTask();
                }
                while (_kick.CurrentCount > 0) await _kick.WaitAsync(stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>One turn of the worker's own work: the hour rows once an hour, sync with the members on the network, then
    /// through the server. It gives way to any request that needs the gate, and goes again soon after.</summary>
    internal async Task RunOnceAsync(CancellationToken stop)
    {
        using var lease = await _gate.EnterBackgroundAsync(stop).ConfigureAwait(false);
        try
        {
            Announce();
            ShowRecoveryCode();
            await ResumeRecoveryAsync(lease.Attention).ConfigureAwait(false);
            await CheckApprovedAsync(lease.Attention).ConfigureAwait(false);  // in a household or not
            if (_store.HouseholdId is null)
            {
                await _relaySync.FlushAsync(_keys, new RelayRun(), lease.Attention).ConfigureAwait(false);   // what leaving left to say
                return;
            }
            SaveSelf(_clock.GetUtcNow());                                      // its name and kind as they are now
            BuildRowsIfDue();
            var run = await _relaySync.RunAsync(_keys, _store.Name, Kind(), lease.Attention).ConfigureAwait(false);   // the server first
            if (run.Removed) CancelPairingUnderWay();                          // plan 0.9: removed, it adds and joins nobody
            if (run.Notices.Count > 0) Publish();                              // the status first, then the App is told
            foreach (var notice in run.Notices) Info(notice);
            Announce();                                                        // a new key, or none, changes the tag
            if (!run.Removed && _store.Session is { } session && _store.RecoveryPut is not null)
            {
                await PutRecoveryCodeAsync(session, lease.Attention).ConfigureAwait(false);   // a new code whose put was lost, or waited
            }
            if (!run.Removed && run.Problem is null) await PollRequestsAsync(lease.Attention).ConfigureAwait(false);
            if (!run.Removed) await SyncOnNetworkAsync(lease.Attention).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Kick();                                                            // gave way: again once the request is done
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>Wakes the worker for a turn soon, as after a pairing, so the server hears of it.</summary>
    internal void Kick() => _kick.Release();

    /// <summary>Carries out a request from the App. Anything that changes the household, pairing and signing in included,
    /// is taken only from a client in the console session, the one at the screen (plan 0.8): another user's session on the
    /// same PC, or a service, may only look for PCs.</summary>
    public async Task<PipeMessage> HandleAsync(PipeRequest request, uint? session, CancellationToken cancel)
    {
        if (request is not BrowsePcsRequest && !_notices.AtTheScreen(session)) return Reply(request.Id, false, NotAtTheScreen);
        try
        {
            return request switch
            {
                BrowsePcsRequest browse => await BrowseAsync(browse, cancel).ConfigureAwait(false),
                AddPcRequest add => await AddPcAsync(add, cancel).ConfigureAwait(false),
                StartCodePairingRequest start => await StartCodePairingAsync(start, cancel).ConfigureAwait(false),
                JoinByCodeRequest join => await JoinByCodeAsync(join).ConfigureAwait(false),
                CancelPairingRequest cancelPairing => await CancelPairingAsync(cancelPairing).ConfigureAwait(false),
                AnswerPromptRequest answer => _prompts.Answer(answer.PromptId ?? "", answer.Accept) || RecoveryCodeSeen(answer.PromptId ?? "")
                    ? Reply(answer.Id, true, "Answered.")
                    : Reply(answer.Id, false, "That question has closed."),
                NewRecoveryCodeRequest newCode => await NewRecoveryCodeAsync(newCode, cancel).ConfigureAwait(false),
                RemovePcRequest remove => await RemoveAsync(remove, cancel).ConfigureAwait(false),
                RemoveOldRowsRequest old => await RemoveOldRowsAsync(old, cancel).ConfigureAwait(false),
                LeaveHouseholdRequest leave => await LeaveAsync(leave, cancel).ConfigureAwait(false),
                RenamePcRequest rename => await RenameAsync(rename, cancel).ConfigureAwait(false),
                SetDiscoverableRequest discoverable => await SetDiscoverableAsync(discoverable, cancel).ConfigureAwait(false),
                SignInRequest signIn => await SignInAsync(signIn, cancel).ConfigureAwait(false),
                SignOutRequest signOut => await SignOutAsync(signOut, cancel).ConfigureAwait(false),
                DeleteAccountRequest delete => await DeleteAccountAsync(delete, cancel).ConfigureAwait(false),
                AskAgainRequest askAgain => await AskAgainAsync(askAgain, cancel).ConfigureAwait(false),
                _ => new ErrorReply(request.Id, "The service does not handle that request."),
            };
        }
        catch (GateTimeout)
        {
            return Reply(request.Id, false, Busy);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            _log.LogError(error, "{Request} failed", request.GetType().Name);
            return Reply(request.Id, false, $"That didn't work: {error.Message}");
        }
        finally
        {
            Publish();
        }
    }

    private async Task<PipeMessage> BrowseAsync(BrowsePcsRequest request, CancellationToken cancel)
    {
        IReadOnlyList<FoundService> found;
        try
        {
            found = await _environment.Discovery.BrowseAsync(_environment.BrowseTime ?? DefaultBrowseTime, cancel).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException error)
        {
            return new ErrorReply(request.Id, error.Message);                  // a Windows too old to find PCs: said so
        }
        var mine = _store.InstanceId;
        var key = _store.CurrentKey;
        var now = _clock.GetUtcNow();
        var pcs = new List<FoundPc>();
        foreach (var service in found.DistinctBy(service => service.Instance)
            .Where(service => service.Instance != mine && service.Txt.GetValueOrDefault("v") == "1" && service.Instance.Length <= 64)
            .Take(MaxFoundKept))
        {
            _found[service.Instance] = (service, now);
            pcs.Add(new FoundPc(service.Instance, NameOf(service), InThisHousehold(service, key)));
        }
        ForgetFound(now);
        return new FoundPcsReply(request.Id, pcs);
    }

    /// <summary>Forgets the PCs found on the network not seen for <see cref="FoundFor"/>, and beyond the
    /// <see cref="MaxFoundKept"/> seen most recently.</summary>
    private void ForgetFound(DateTimeOffset now)
    {
        var kept = _found.ToArray().OrderByDescending(pair => pair.Value.Seen).ToList();
        foreach (var (instance, found) in kept.Skip(MaxFoundKept).Concat(kept.Take(MaxFoundKept).Where(pair => now - pair.Value.Seen >= FoundFor)))
        {
            _found.TryRemove(new KeyValuePair<string, (FoundService, DateTimeOffset)>(instance, found));
        }
    }

    private async Task<PipeMessage> AddPcAsync(AddPcRequest request, CancellationToken cancel)
    {
        ForgetFound(_clock.GetUtcNow());
        if (request.InstanceId is null || !_found.TryGetValue(request.InstanceId, out var seen) || seen.Service is not { Address: not null } pc)
        {
            return Reply(request.Id, false, "That PC isn't on the network any more. Look again.");
        }
        var name = NameOf(pc);
        if (InThisHousehold(pc, _store.CurrentKey)) return Reply(request.Id, false, $"{name} is already in this household.");
        if (BeginPairing(out var refusal) is not { } pairing) return Reply(request.Id, false, refusal!);
        await Task.CompletedTask.ConfigureAwait(false);
        Track(AddOnNetworkAsync(pc, name, pairing));
        return Reply(request.Id, true, $"Connecting to {name}.");
    }

    private async Task AddOnNetworkAsync(FoundService pc, string name, CurrentPairing pairing)
    {
        using (pairing)
        {
            PairingOutcome outcome;
            try
            {
                await using var channel = await LanConnector.ConnectAsync(pc.Address!, pc.Port, ConnectTimeout, pairing.Token).ConfigureAwait(false);
                outcome = await PairingSession.AddAsync(channel, Identity(), pc.Instance, _prompts, WelcomeForAsync, RecordAsync, _timeouts, pairing.Token)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                outcome = new PairingOutcome.Failed($"Couldn't reach {name}. Check it's on and on the same network.");
            }
            catch (GateTimeout)
            {
                outcome = new PairingOutcome.Failed($"This PC was busy, so {name} wasn't added. Try again.");
            }
            catch (OperationCanceledException) when (pairing.Token.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                outcome = new PairingOutcome.Refused($"Adding {name} was cancelled.");
            }
            Added(outcome);
        }
    }

    private async Task<PipeMessage> StartCodePairingAsync(StartCodePairingRequest request, CancellationToken cancel)
    {
        if (BeginPairing(out var refusal, madeCode: true) is not { } pairing) return Reply(request.Id, false, refusal!);
        CodeMeeting? meeting;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, pairing.Token);
            limit.CancelAfter(GateWait);
            meeting = await _codePairing.OpenAsync(Identity(), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            meeting = null;
        }
        if (meeting is null)
        {
            pairing.Dispose();
            return Reply(request.Id, false, "Couldn't reach the server to make a code. Check this PC is online and try again.");
        }
        Track(AddByCodeAsync(meeting, pairing));
        return Reply(request.Id, true, "Type this code on the other PC within 10 minutes.", meeting.Code);
    }

    private async Task AddByCodeAsync(CodeMeeting meeting, CurrentPairing pairing)
    {
        using (pairing)
        using (meeting)
        {
            PairingOutcome outcome;
            try
            {
                outcome = await _codePairing.AddAsync(meeting, Identity(), WelcomeForAsync, RecordAsync, pairing.Token).ConfigureAwait(false);
            }
            catch (GateTimeout)
            {
                outcome = new PairingOutcome.Failed("This PC was busy, so the other PC wasn't added. Try again.");
            }
            Added(outcome);
        }
    }

    /// <summary>Joins by code; a code this PC made and still waits on is stopped first, since its user is joining instead.</summary>
    private async Task<PipeMessage> JoinByCodeAsync(JoinByCodeRequest request)
    {
        if (PairingCode.Normalize(request.Code) is null)
        {
            return Reply(request.Id, false, "That isn't a code. A code has 16 letters and digits, like K7QM-2XHD-9PW4-R8TA.");
        }
        await StopOwnCodeAsync().ConfigureAwait(false);
        if (BeginPairing(out var refusal) is not { } pairing) return Reply(request.Id, false, refusal!);
        Track(JoinByCodeAsync(request.Code, pairing));
        return Reply(request.Id, true, "Looking for the PC that made that code.");
    }

    private async Task JoinByCodeAsync(string code, CurrentPairing pairing)
    {
        using (pairing)
        {
            PairingOutcome outcome;
            try
            {
                outcome = await _codePairing.JoinAsync(code, Identity(), _prompts, _store.HouseholdId is not null,
                        (welcome, adder) => EnterAsync(welcome, adder, pairing), pairing.Token)
                    .ConfigureAwait(false);
            }
            catch (GateTimeout)
            {
                outcome = new PairingOutcome.Failed("This PC was busy, so it didn't join. Try again.");
            }
            if (outcome is PairingOutcome.Refused or PairingOutcome.Failed { Text: "That code doesn't match the other PC's, so nothing was changed." })
            {
                _pairingGate.Refused();
            }
            Progress(outcome.Text, (outcome as PairingOutcome.Joined)?.Other.Name, null);
        }
    }

    /// <summary>A connection from another PC on the network: a pairing asks this PC's user; a sync is with a member. Pairings
    /// that come to nothing count against the address they came from, and too many leave it unanswered for a while; and
    /// only a few notices about them go to the App, so a stranger can't fill the tray.</summary>
    private async Task OnConnectionAsync(LanCall call, CancellationToken cancel)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stopping.Token);
        var (channel, hello) = (call.Channel, call.Hello);
        if (call.Message.Purpose == Hello.Sync)
        {
            await FreshenMembersAsync(stopping.Token).ConfigureAwait(false);
            if (_store.HouseholdId is { } householdId)
            {
                await _lanSync.RespondAsync(channel, hello, Identity(), stopping.Token, () => _store.HouseholdId == householdId).ConfigureAwait(false);
            }
            return;
        }
        if (call.Message.Purpose != Hello.Pair) return;
        if (call.From is { } address && !_strangers.Allowed(address)) return;
        if (BeginPairing(out _, also: stopping.Token) is not { } pairing)
        {
            if (call.From is { } busyFrom) _strangers.Failed(busyFrom);
            return;                                                            // closed without a word: not even this PC's hello
        }
        using (pairing)
        {
            PairingOutcome outcome;
            var counted = false;
            void Refused()
            {
                counted = true;                                                // before the answer goes, so the next try meets it
                if (call.From is { } refusedFrom) _strangers.Failed(refusedFrom);
            }
            try
            {
                outcome = await PairingSession.JoinAsync(
                    channel, hello, Identity(), _prompts, _store.HouseholdId is not null, (welcome, adder) => EnterAsync(welcome, adder, pairing), _timeouts,
                    pairing.Token, Refused)
                    .ConfigureAwait(false);
            }
            catch (GateTimeout)
            {
                outcome = new PairingOutcome.Failed("This PC was busy, so it didn't join. Try again.");
            }
            if (outcome is not PairingOutcome.Joined && !counted && call.From is { } from) _strangers.Failed(from);   // network ones never pause code pairing
            if (outcome is PairingOutcome.Joined || (outcome is PairingOutcome.Failed && _strangers.MayTell())) Info(outcome.Text);
        }
    }

    /// <summary>The welcome for a PC being added, making the household first when this PC is in none (households design
    /// §1: it is made when a PC adds its first other PC). No old key goes to a newcomer (plan 0.10): a new key waiting for the
    /// server goes first; null while it can't.</summary>
    private async Task<Welcome?> WelcomeForAsync(MemberInfo joiner)
    {
        using var entered = await EnterGateAsync(_stopping.Token, PairingGateWait).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        if (_store.HouseholdId is null || _store.CurrentKey is null)
        {
            var householdId = Wire.NewHouseholdId();
            ClearOthers();
            KeepOnlyRemovalsOfOthers(householdId);
            _store.EnterHousehold(householdId, 1, HouseholdCrypto.NewKey());
            SaveSelf(now);
            BuildRows(now);
            _store.PostedThrough = _household.LatestChange(_keys.DeviceId);   // the year goes as the first snapshot
            _store.AddPending(new PendingOp(PendingOp.Create, householdId));
            _log.LogInformation("Made a household to add {Name} to", joiner.Name);
        }
        else if (!await RotationFirstAsync(_store.HouseholdId, joiner.Id, _stopping.Token).ConfigureAwait(false))
        {
            return null;
        }
        var members = _household.Members()
            .Where(member => _members.Current(member.DeviceId) is not null && member.DeviceId != joiner.Id)
            .Select(member => new MemberInfo(member.DeviceId, member.Name, member.Kind, member.SignKey, member.DhKey))
            .Take(Wire.MaxMembers)
            .ToList();
        return new Welcome(_store.HouseholdId!, _store.Epoch, _store.CurrentKey!, members, [.. _members.Entries().Where(entry => entry.Id != joiner.Id)]);
    }

    /// <summary>
    /// No old key to a newcomer (plan 0.10): a new key waiting for the server goes to it before a welcome or an approval
    /// hands out the household's key, and a PC the server removed at this PC's epoch comes back only at a newer one, so a new
    /// key is made for it first.
    /// </summary>
    /// <returns>False while the new key can't go, as with the server out of reach.</returns>
    private async Task<bool> RotationFirstAsync(string householdId, string device, CancellationToken cancel)
    {
        if (_members.ServerEntryOf(device) is { Removed: { } removed } && removed >= _store.Epoch) _relaySync.StartRotation(householdId);
        if (!_relaySync.RotationPending) return true;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(GateWait);
        return await _relaySync.FinishRotationAsync(_keys, limit.Token).ConfigureAwait(false);
    }

    /// <summary>Records a PC this one is adding, in one step before it is told it is in (plan 0.10): the add queued for the
    /// server, and the new member's keys introduced here. The pairing's cancel stops it only while it waits for the gate:
    /// before the step, nothing is left behind.</summary>
    /// <returns>What went wrong when it couldn't be added, as when this PC's household is no longer the welcome's; else null.</returns>
    private async Task<string?> RecordAsync(Welcome welcome, MemberInfo joiner, byte[] proof, CancellationToken cancel)
    {
        using (await EnterGateAsync(cancel, PairingGateWait).ConfigureAwait(false))
        {
            if (_store.HouseholdId != welcome.HouseholdId)
            {
                return $"This PC's household changed while {joiner.Name} was joining, so it wasn't added. Try again.";
            }
            _store.AddPending(new PendingOp(PendingOp.Add, welcome.HouseholdId, Sign: Wire.Encode(joiner.Sign), Dh: Wire.Encode(joiner.Dh),
                Proof: Wire.Encode(proof)));
            _members.Introduce(joiner, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            Announce();
        }
        Kick();
        return null;
    }

    /// <summary>Tells the App how a pairing this PC started ended. Its user's own adds never count toward pausing pairing.</summary>
    private void Added(PairingOutcome outcome)
    {
        Publish();                                                             // the status first, then the App is told
        Progress(outcome.Text, (outcome as PairingOutcome.Joined)?.Other.Name, null);
    }

    /// <summary>Takes this PC into the household in a welcome it accepted. A PC in another household leaves that one first,
    /// as a PC removing itself does; the household's other PCs and their rows go with it, and this PC's year is built for
    /// the new one.</summary>
    /// <returns>False when this PC entered or left a household while it was joining this one (plan 0.9): it doesn't join too.</returns>
    private async Task<bool> EnterAsync(Welcome welcome, MemberInfo adder, CurrentPairing pairing)
    {
        using (await EnterGateAsync(_stopping.Token, PairingGateWait).ConfigureAwait(false))
        {
            if (_store.HouseholdId != pairing.StartHousehold && _store.HouseholdId != welcome.HouseholdId)
            {
                _log.LogInformation("This PC's household changed while it was joining {Name}'s, so it didn't join", adder.Name);
                return false;
            }
            EnterLocked(welcome.HouseholdId, welcome.Epoch, welcome.Key, welcome.AllEntries, adder.Id, adder);
            _log.LogInformation("Joined {Name}'s household", adder.Name);
        }
        Kick();
        return true;
    }

    /// <summary>Takes this PC into a household with its key, inside the gate, leaving any other first (see
    /// <see cref="EnterAsync"/>), at <paramref name="epoch"/>. The PC that let it in is introduced by the pairing or the
    /// approval itself, and the members in <paramref name="entries"/> by its list (plan 0.10), until this PC reads the
    /// server's. In a household new to it, this PC's year goes as its first snapshot.</summary>
    /// <param name="fromId">The PC whose list it is, which gives its own name and kind; null for a recovery's.</param>
    /// <param name="introduce">The PC that let this one in, with the keys the pairing or approval checked.</param>
    private void EnterLocked(string householdId, int epoch, byte[] key, IReadOnlyList<WireMember> entries, string? fromId, MemberInfo? introduce)
    {
        var now = _clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var entering = _store.HouseholdId != householdId;
        if (entering)
        {
            if (_store.HouseholdId is not null) LeaveLocked(now);
            ClearOthers();
            KeepOnlyRemovalsOfOthers(householdId);
            _store.EnterHousehold(householdId, epoch, key);
        }
        else
        {
            _store.AddKey(epoch, key);
            _store.RelayConfirmed = false;                                     // added again, maybe after a removal it hadn't heard of
            _store.ServerMembers = null;                                       // its list is read again
            _store.MembersCheckedAt = null;
        }
        _store.AskedToJoin = null;
        SaveSelf(now);
        if (introduce is not null) _members.Introduce(introduce, nowMs);
        _members.Learn(entries, fromId, _keys.DeviceId, nowMs);
        BuildRows(now);
        if (entering) _store.PostedThrough = _household.LatestChange(_keys.DeviceId);
        Announce();
        Publish();
    }

    private async Task<PipeMessage> RemoveAsync(RemovePcRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        if (_store.HouseholdId is null) return Reply(request.Id, false, NotInOne);
        if (request.DeviceId == _keys.DeviceId) return Reply(request.Id, false, "To take this PC out, leave the household.");
        if (request.DeviceId is null || _household.Member(request.DeviceId) is not { } member)
        {
            return Reply(request.Id, false, "That PC isn't in this household.");
        }
        var householdId = _store.HouseholdId;
        var removing = _store.Pending.Any(op => op.Kind == PendingOp.Remove && op.Household == householdId && op.Device == member.DeviceId);
        if (removing || (!_members.ServerCurrent(member.DeviceId) && _members.Current(member.DeviceId) is null))
        {
            _members.ForgetRows(member.DeviceId);                              // what the server and the lists say of it stays
            return Reply(request.Id, true, $"{member.Name}'s rows were removed.");
        }

        // Households design §6, plan 0.10: the removal goes to the server before any new key, even for a PC already shown as
        // left that the server still lists; then a new key under the next epoch, sealed to the members the server lists as
        // current once the removal is in, this PC among them, so the server moves to the epoch even when no other PC stays.
        // This PC takes the new key on once the server has taken it.
        _members.Remove(member.DeviceId, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        var pending = _store.Pending.ToList();
        var keysAt = pending.FindIndex(op => op.Kind == PendingOp.Keys && op.Household == householdId);
        pending.Insert(keysAt < 0 ? pending.Count : keysAt, new PendingOp(PendingOp.Remove, householdId, Device: member.DeviceId));
        _store.Pending = pending;
        _relaySync.StartRotation(householdId, forRemoval: true);
        Announce();
        Kick();
        _log.LogInformation("Removed {Name} from the household; a new key waits to go to the server", member.Name);
        return Reply(request.Id, true, $"{member.Name} was removed from the household.");
    }

    /// <summary>
    /// Deletes the rows this PC keeps from PCs no longer in its household (plan 0.8, "Old rows"): the one named, which must
    /// have left or been removed, or with none named every such PC's, which after leaving is all of them, this PC's own
    /// too. What the server and the lists' removals say of a PC stays, so it is never taken back on another PC's word.
    /// </summary>
    private async Task<PipeMessage> RemoveOldRowsAsync(RemoveOldRowsRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        var withRows = _household.Latest().Keys.ToHashSet(StringComparer.Ordinal);
        if (request.DeviceId is { } device)
        {
            if (device == _keys.DeviceId) return Reply(request.Id, false, "This PC's own rows stay while it is in the household.");
            if (_store.HouseholdId is not null && _members.MaySync(device) && _household.Member(device) is { } current)
            {
                return Reply(request.Id, false, $"{current.Name} is still in the household. Remove it first.");
            }
            var known = _household.Member(device);
            if (known is null && !withRows.Contains(device)) return Reply(request.Id, false, "That PC has no rows on this PC.");
            _members.ForgetRows(device);
            return Reply(request.Id, true, known is null ? "That PC's rows were removed." : $"{known.Name}'s rows were removed.");
        }
        var stay = _store.HouseholdId is null
            ? []
            : _household.Members().Where(member => _members.MaySync(member.DeviceId)).Select(member => member.DeviceId).Append(_keys.DeviceId)
                .ToHashSet(StringComparer.Ordinal);
        var old = withRows.Union(_household.Members().Select(member => member.DeviceId)).Where(id => !stay.Contains(id)).ToList();
        foreach (var id in old) _members.ForgetRows(id);
        if (_store.HouseholdId is null) _rowsBuiltForHour = -1;
        return Reply(request.Id, true, old.Count == 0 ? "There were no old rows to remove." : "The old rows were removed.");
    }

    private async Task<PipeMessage> LeaveAsync(LeaveHouseholdRequest request, CancellationToken cancel)
    {
        await StopPairingAsync().ConfigureAwait(false);                         // plan 0.9: a pairing under way is stopped first
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        if (_store.HouseholdId is null) return Reply(request.Id, false, NotInOne);
        LeaveLocked(_clock.GetUtcNow());
        Announce();
        Kick();
        return Reply(request.Id, true, "This PC left the household.");
    }

    /// <summary>Leaves the household, inside the gate: this PC takes itself off the server and forgets the household (plan
    /// 0.8). It makes no new key: a PC leaving can't vouch for who stays, so each member that stays makes one when it sees
    /// the removal.</summary>
    private void LeaveLocked(DateTimeOffset now)
    {
        var householdId = _store.HouseholdId!;
        _store.AddPending(new PendingOp(PendingOp.Remove, householdId, Device: _keys.DeviceId));
        Membership.Forget(_store, _household, now.ToUnixTimeMilliseconds());
        _log.LogInformation("Left the household {Household}", householdId);
    }

    private async Task<PipeMessage> RenameAsync(RenamePcRequest request, CancellationToken cancel)
    {
        var name = Wire.Name(request.Name);
        if (name is null || request.Name!.Trim().Length > Wire.MaxName) return Reply(request.Id, false, "A name has 1 to 40 characters.");
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        _store.Name = name;
        if (_store.HouseholdId is not null) SaveSelf(_clock.GetUtcNow());
        Announce();
        return Reply(request.Id, true, $"This PC is now called {name}.");
    }

    private async Task<PipeMessage> SetDiscoverableAsync(SetDiscoverableRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        _store.Discoverable = request.On;
        Announce();
        return Reply(request.Id, true, request.On ? "Other PCs on your network can find this one." : "Other PCs on your network can no longer find this one.");
    }

    /// <summary>
    /// Syncs directly with each member found on the network, whose tag shows it is in this household: once each, however
    /// often it is announced, at most <see cref="MaxFound"/> of them, and within <see cref="LanBudget"/> in all, after the
    /// server's sync (plan 0.8), so members on the network, or announcements posing as them, can't hold the turn up.
    /// </summary>
    private async Task SyncOnNetworkAsync(CancellationToken cancel)
    {
        var key = _store.CurrentKey;
        if (key is null) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(LanBudget);
        try
        {
            IReadOnlyList<FoundService> found;
            try
            {
                found = await _environment.Discovery.BrowseAsync(_environment.BrowseTime ?? DefaultBrowseTime, budget.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log.LogDebug(error, "Browsing the network failed");
                return;
            }
            var members = found
                .Where(service => service.Instance != _store.InstanceId && service.Address is not null && InThisHousehold(service, key))
                .DistinctBy(service => service.Instance)
                .Take(MaxFound)
                .ToList();
            if (members.Count == 0) return;
            if (Took(await _relaySync.FreshenMembersAsync(_keys, MembersFreshFor, budget.Token).ConfigureAwait(false))) return;   // plan 0.10: the list first
            foreach (var member in members)
            {
                try
                {
                    await using var channel = await LanConnector.ConnectAsync(member.Address!, member.Port, ConnectTimeout, budget.Token).ConfigureAwait(false);
                    var outcome = await _lanSync.SyncAsync(channel, Identity(), budget.Token).ConfigureAwait(false);
                    _log.LogDebug("Synced with {Instance} on the network: {Outcome}", member.Instance, outcome);
                }
                catch (IOException error)
                {
                    _log.LogDebug(error, "A member on the network couldn't be reached");
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            _log.LogDebug("Syncing on the network took its whole time this turn; the rest goes next turn");
        }
    }

    /// <summary>Before a sync that came in, reads the member list again when it is older than <see cref="MembersFreshFor"/>
    /// (plan 0.10): inside the gate, and within <see cref="FreshenWait"/>, after which the sync goes on with the list as it is.</summary>
    private async Task FreshenMembersAsync(CancellationToken cancel)
    {
        if (_store.HouseholdId is null || _store.MembersCheckedAt is { } checkedAt
            && _clock.GetUtcNow().ToUnixTimeMilliseconds() - checkedAt < (long)MembersFreshFor.TotalMilliseconds)
        {
            return;
        }
        try
        {
            using (await EnterGateAsync(cancel, FreshenWait).ConfigureAwait(false))
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(FreshenWait);
                Took(await _relaySync.FreshenMembersAsync(_keys, MembersFreshFor, limit.Token).ConfigureAwait(false));
            }
        }
        catch (GateTimeout)
        {
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>Tells the App what reading the member list found.</summary>
    /// <returns>True when it found this PC removed.</returns>
    private bool Took(RelayRun run)
    {
        if (run.Removed) CancelPairingUnderWay();
        foreach (var notice in run.Notices) Info(notice);
        return run.Removed;
    }

    private void BuildRowsIfDue()
    {
        var now = _clock.GetUtcNow();
        if (HourOf(now) == _rowsBuiltForHour) return;
        BuildRows(now);
    }

    /// <summary>Builds this PC's rows up to now. Change times left far ahead by a clock that ran fast go back to now first,
    /// once (plan 0.9), and those rows go to the server again.</summary>
    private void BuildRows(DateTimeOffset now)
    {
        if (_rows.Rebase(_keys.DeviceId, now))
        {
            _store.PostedThrough = Math.Min(_store.PostedThrough, now.ToUnixTimeMilliseconds() - 1);
            _store.PostedHour = null;
            _log.LogInformation("This PC's rows had change times far ahead, left by a clock that ran fast; they were moved back to now");
        }
        _rows.Build(_keys.DeviceId, HourRows.BackfillFrom(now), now);
        _rowsBuiltForHour = HourOf(now);
    }

    private static long HourOf(DateTimeOffset time) => time.ToUnixTimeMilliseconds() / 3_600_000;

    /// <summary>Entering <paramref name="householdId"/> drops what other households still had to hear (plan 0.9), but their
    /// removals, this PC's own leaving among them: a household that never hears of one keeps the PC in it.</summary>
    private void KeepOnlyRemovalsOfOthers(string householdId) =>
        _store.Pending = [.. _store.Pending.Where(op => op.Household == householdId || op.Kind == PendingOp.Remove)];

    /// <summary>Removes every other PC and its rows, as when this PC enters another household.</summary>
    private void ClearOthers()
    {
        foreach (var member in _household.Members().Where(member => member.DeviceId != _keys.DeviceId)) _household.DeleteMember(member.DeviceId);
        foreach (var device in _household.Latest().Keys.Where(device => device != _keys.DeviceId)) _household.DeleteRows(device);
    }

    private void SaveSelf(DateTimeOffset now) => _household.SaveMember(new HouseholdMember(
        _keys.DeviceId, _store.Name, Kind(), _keys.SignPublic, _keys.DhPublic, now.ToUnixTimeMilliseconds(), null, null));

    private PairingIdentity Identity() => new(_keys, _store.Name, Kind(), _store.InstanceId);

    /// <summary>Laptop or desktop, as the user's profile says, or as detected.</summary>
    private ChassisKind Kind() => _board.Settings?.Profile.Chassis ?? _board.Facts?.Chassis ?? ChassisKind.Desktop;

    private static string NameOf(FoundService service) => Wire.Name(service.Txt.GetValueOrDefault("name")) ?? "A PC";

    private static bool InThisHousehold(FoundService service, byte[]? key) =>
        key is not null && service.Txt.GetValueOrDefault("tag") is { Length: > 0 } tag && tag == HouseholdCrypto.HouseholdTag(key, service.Instance);

    /// <summary>Announces this PC as it is now: its name, and the tag of its household when it is in one. Only while it may be
    /// found, as its user lets it be and on a Private network, does it listen at all (plan 0.8); otherwise its port is shut
    /// and nothing is announced.</summary>
    private void Announce()
    {
        if (_stopping.IsCancellationRequested) return;
        if (Listen() is not { Port: > 0 } listener)
        {
            _announcer.Update(null);
            return;
        }
        var instance = _store.InstanceId;
        var key = _store.CurrentKey;
        var txt = new Dictionary<string, string>
        {
            ["v"] = "1",
            ["name"] = _store.Name,
            ["tag"] = key is null ? "" : HouseholdCrypto.HouseholdTag(key, instance),
        };
        _announcer.Update(new Announcement(instance, listener.Port, txt));
    }

    /// <summary>Starts the listener when this PC may be found and stops it when it may not; the listener as it is now.</summary>
    private LanListener? Listen()
    {
        var wanted = _store.Discoverable && OnPrivateNetwork();
        LanListener? stopping = null;
        LanListener? listening;
        lock (_listening)
        {
            if (wanted && _listener is null)
            {
                var started = new LanListener(_environment.ListenAddress, OnConnectionAsync, _log);
                try
                {
                    started.Start();
                    _listener = started;
                }
                catch (System.Net.Sockets.SocketException error)
                {
                    _log.LogWarning(error, "The household's listener couldn't start, so other PCs can't reach this one on the network");
                    Track(started.DisposeAsync().AsTask());
                }
            }
            else if (!wanted && _listener is not null)
            {
                (stopping, _listener) = (_listener, null);
                _log.LogInformation("Stopped listening for the household's other PCs");
            }
            listening = _listener;
        }
        if (stopping is not null) Track(stopping.DisposeAsync().AsTask());
        return listening;
    }

    private bool OnPrivateNetwork()
    {
        try
        {
            return _environment.Network.IsPrivate;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How the household stands, for the status. It never throws: the status keeps what was last published.</summary>
    private void Publish()
    {
        try
        {
            var me = _keys.DeviceId;
            var householdId = _store.HouseholdId;
            var withRows = _household.Latest();
            IReadOnlyList<MemberStatus> members = householdId is null
                ? []
                : [.. _household.Members().Where(member => member.LeftMs is null || withRows.ContainsKey(member.DeviceId)).Select(member => new MemberStatus(
                    member.DeviceId, member.Name, member.Kind, member.DeviceId == me,
                    member.DeviceId == me || member.LastSyncedMs is not { } synced ? null : DateTimeOffset.FromUnixTimeMilliseconds(synced),
                    member.LeftMs is not null))];
            _board.Publish(new HouseholdStatus(
                householdId, me, _store.Name, Kind(), _store.Discoverable, members, householdId is null ? null : _store.Problem,
                SignedIn: _store.Session is not null, PendingApprovals: householdId is null ? 0 : Volatile.Read(ref _waitingApprovals),
                RecoveryMissing: householdId is not null && _store.Session is not null && _recoveryMissing,
                CanAskAgain: householdId is null && _store.Session is not null && _store.CanAskAgain));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "How the household stands could not be read for the status");
        }
    }

    private void Progress(string text, string? fromName, string? code) =>
        _notices.Publish(new HouseholdNotice(NoticeKind.PairingProgress, null, text, fromName, code, null));

    private void Info(string text) => _notices.Publish(new HouseholdNotice(NoticeKind.Info, null, text, null, null, null));

    /// <summary>Enters the gate within <paramref name="wait"/>, <see cref="GateWait"/> by default, or throws <see cref="GateTimeout"/>.</summary>
    private async Task<IDisposable> EnterGateAsync(CancellationToken cancel, TimeSpan? wait = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(wait ?? GateWait);
        try
        {
            return await _gate.EnterAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new GateTimeout();
        }
    }

    private void Track(Task work)
    {
        _running[work] = 0;
        _ = work.ContinueWith(
            done =>
            {
                _running.TryRemove(done, out _);
                if (done.Exception is { } error) _log.LogError(error, "A household pairing failed");
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunOnceSafelyAsync(CancellationToken stop)
    {
        try
        {
            await RunOnceAsync(stop).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            _log.LogError(error, "The household's 15-minute run failed");
        }
    }

    private static HouseholdReply Reply(long id, bool ok, string message, string? code = null) => new(id, ok, message, code);

    /// <summary>The gate stayed shut longer than a request may wait.</summary>
    private sealed class GateTimeout() : Exception(Busy);

}
