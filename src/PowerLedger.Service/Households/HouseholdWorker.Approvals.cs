using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;

namespace PowerLedger.Service.Households;

/// <summary>N2: the approval of a waiting PC this PC runs (plan 0.9): the PC, this PC's nonce, committed to on the server, and
/// once its user approved, the body sealed at an epoch, kept so a retry is the very same request.</summary>
internal sealed record Approving(string Household, string Device, string Nonce, bool Accepted = false, int? Epoch = null, string? Body = null);

/// <summary>N2: this PC's answer to the member that committed to approving it (plan 0.9): that member's device and keys, its
/// commitment, the nonce this PC sent back, and whether this PC's user has said the two codes match.</summary>
internal sealed record Answering(string Household, string Approver, string Sign, string Dh, string Commit, string Nonce, bool Confirmed = false);

/// <summary>
/// N2's sealed key lists (plan 0.9): the household key and a member list, as an approval and a recovery carry them, at most
/// 16384 characters as posted. A list too long for that goes without its earliest removals; the server's member list keeps
/// every removal.
/// </summary>
internal static class KeyLists
{
    public const int MaxBody = 16384;

    /// <summary>The body as <paramref name="seal"/> makes it from the list's bytes, cut to fit; null when even the current
    /// members alone don't.</summary>
    public static string? Seal(byte[] key, List<WireMember> members, Func<byte[], byte[]> seal)
    {
        var list = members.ToList();
        while (true)
        {
            var body = Wire.Encode(seal(HouseholdJson.Bytes(new SealedKeyList(Wire.Encode(key), list), HouseholdJson.Default.SealedKeyList)));
            if (body.Length <= MaxBody) return body;
            var last = list.FindLastIndex(entry => entry.RemovedEpoch is { } removed && (entry.AddedEpoch ?? 0) <= removed);
            if (last < 0) return null;
            list.RemoveAt(last);
        }
    }

    /// <summary>The key and list in opened bytes; null when they aren't one.</summary>
    public static (byte[] Key, List<WireMember> Members)? Read(byte[] opened) =>
        HouseholdJson.Read(opened, HouseholdJson.Default.SealedKeyList) is { } list && Wire.Decode(list.Key) is { Length: HouseholdCrypto.KeyLength } key
            ? (key, list.Members ?? [])
            : null;
}

/// <summary>N2's approval (plan 0.9): the household key and the approving PC's member list, sealed to the waiting PC as a key
/// is, under a context naming the household and the epoch.</summary>
internal static class Approval
{
    public static string Context(string householdId, int epoch) => $"household {householdId} approval epoch {epoch}";

    public static string? Seal(DeviceKeys me, byte[] theirDh, string householdId, int epoch, byte[] key, List<WireMember> members) =>
        KeyLists.Seal(key, members, plain => HouseholdCrypto.WrapFor(me.Dh, theirDh, plain, Context(householdId, epoch)));

    /// <summary>The key and list in an approval sealed by the PC with key-agreement key <paramref name="fromDh"/>; null when
    /// it doesn't open.</summary>
    public static (byte[] Key, List<WireMember> Members)? Open(DeviceKeys me, byte[] fromDh, string body, string householdId, int epoch)
    {
        if (Wire.Decode(body) is not { } sealedBody) return null;
        try
        {
            return KeyLists.Read(HouseholdCrypto.UnwrapFrom(me.Dh, fromDh, sealedBody, Context(householdId, epoch)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// N2's approvals (households design §7, plan 0.9), on the household worker: a PC signed in as the account joins once a
/// member approves it, commit then reveal, so both screens show the approval code before anything is sealed, as with
/// Signal's or WhatsApp's linked devices.
/// </summary>
internal sealed partial class HouseholdWorker
{
    /// <summary>What an approved PC is called here until its first batch gives its name.</summary>
    internal const string NewPcName = "New PC";

    /// <summary>At most this many approvals a member starts in a UTC day (plan 0.9).</summary>
    internal const int ApprovalsADay = 5;

    internal const string AskedAgainButIn = "This PC is already in a household.";

    private int _waitingApprovals;

    /// <summary>1 while this PC's user is asked about the approval it runs, so it isn't asked twice.</summary>
    private int _askingApproval;

    /// <summary>1 while this PC's user is asked to check the approval code, so it isn't asked twice.</summary>
    private int _confirmingJoin;

    /// <summary>When this PC last linked the account to its household, as a check that the link is still there.</summary>
    private DateTimeOffset? _linkedAt;

    /// <summary>
    /// With its sync, a member signed in follows the PCs waiting to join (plan 0.9), one approval at a time and at most
    /// <see cref="ApprovalsADay"/> new ones a day: it commits to a nonce for the first no member has committed to; once that
    /// PC answers with its own, it reveals its nonce and asks its user, showing the code both PCs work out; an approval its
    /// user made that didn't go yet goes again. With nobody at the screen to ask, it doesn't look.
    /// </summary>
    private async Task PollRequestsAsync(CancellationToken cancel)
    {
        if (_store.Session is null || _store.HouseholdId is not { } householdId)
        {
            Volatile.Write(ref _waitingApprovals, 0);
            return;
        }
        await LinkAgainIfDueAsync(householdId, cancel).ConfigureAwait(false);
        if (!_notices.AnyoneAtTheScreen) return;
        var result = await _environment.Relay.RequestsAsync(_keys, householdId, cancel).ConfigureAwait(false);
        if (!result.Ok) return;
        var waiting = result.Value!.Where(item => item.Device != _keys.DeviceId && Wire.IsDeviceId(item.Device)
                && Wire.PublicKey(item.Sign) is { } sign && HouseholdCrypto.DeviceIdOf(sign) == item.Device && Wire.PublicKey(item.Dh) is not null)
            .Take(Wire.MaxMembers)
            .ToList();
        Volatile.Write(ref _waitingApprovals, waiting.Count);
        if (_store.Approving is { } approving && approving.Household == householdId)
        {
            if (waiting.FirstOrDefault(item => item.Device == approving.Device) is { } item && item.Approver == _keys.DeviceId && item.Commit == Commit(approving.Nonce))
            {
                await MoveApprovalOnAsync(householdId, item, approving, cancel).ConfigureAwait(false);
                return;                                                        // one at a time
            }
            _store.Approving = null;                                           // approved, turned away, lapsed or asked again since
        }
        if (waiting.FirstOrDefault(item => item.Approver is null) is not { } next || !MayStartApproval()) return;
        var nonce = Wire.Encode(HouseholdCrypto.NewNonce());
        _store.Approving = new Approving(householdId, next.Device, nonce);    // kept before it goes: a commit is never left without its nonce
        var committed = await _environment.Relay.CommitAsync(_keys, householdId, next.Device, Commit(nonce), cancel).ConfigureAwait(false);
        if (committed.Ok) StartedApproval();
        else if (!committed.Transient) _store.Approving = null;               // another member came first, or it no longer waits
    }

    /// <summary>The approval under way, one step on: reveal once the waiting PC's nonce is in, then ask the user, or approve
    /// again when the user already did.</summary>
    private async Task MoveApprovalOnAsync(string householdId, JoinRequestItem item, Approving approving, CancellationToken cancel)
    {
        if (Wire.Decode(item.Nonce) is not { Length: 32 }) return;              // the waiting PC hasn't answered yet
        if (item.Reveal is null)
        {
            if (!(await _environment.Relay.RevealAsync(_keys, householdId, item.Device, approving.Nonce, cancel).ConfigureAwait(false)).Ok) return;
        }
        else if (item.Reveal != approving.Nonce)
        {
            _store.Approving = null;
            return;
        }
        if (approving.Accepted)
        {
            await ApproveAsync(householdId, item, cancel).ConfigureAwait(false);
            return;
        }
        if (Interlocked.CompareExchange(ref _askingApproval, 1, 0) != 0) return;
        Track(AskToApproveAsync(householdId, item, approving));
    }

    /// <summary>Asks the user whether to let the waiting PC in, showing the approval code over both PCs' keys and nonces, which
    /// the waiting PC shows too (plan 0.9). Approve seals the key; Don't approve turns the PC away; a prompt closed unanswered
    /// comes back at a later turn and never counts as a no.</summary>
    private async Task AskToApproveAsync(string householdId, JoinRequestItem item, Approving approving)
    {
        try
        {
            var asYou = item.Account is not null && item.Account == _store.Account;
            var code = HouseholdCrypto.ApprovalCode(Wire.PublicKey(item.Sign)!, Wire.PublicKey(item.Dh)!, _keys.SignPublic, _keys.DhPublic,
                Wire.Decode(item.Nonce)!, Wire.Decode(approving.Nonce)!);
            var answer = await _prompts.AskToApproveAsync(asYou, code, _stopping.Token).ConfigureAwait(false);
            if (answer is null) return;
            using (await EnterGateAsync(_stopping.Token, PairingGateWait).ConfigureAwait(false))
            {
                if (_store.Approving != approving || _store.HouseholdId != householdId) return;   // moved on meanwhile
                if (answer == false)
                {
                    await DenyAsync(householdId, item).ConfigureAwait(false);
                    _store.Approving = null;
                    return;
                }
                _store.Approving = approving with { Accepted = true };
                await ApproveAsync(householdId, item, _stopping.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _askingApproval, 0);
            Publish();
        }
    }

    /// <summary>
    /// Seals the current key and this PC's member list to the waiting PC and posts the approval, which makes it a member,
    /// added here at this PC's epoch (plan 0.9). The body sealed at an epoch is kept, so a retry is the very same request;
    /// when the request or the key changed meanwhile, this PC catches up, and the approval is sealed again at a later turn.
    /// </summary>
    private async Task ApproveAsync(string householdId, JoinRequestItem item, CancellationToken cancel)
    {
        if (_store.Approving is not { Accepted: true } approving || _store.CurrentKey is not { } key
            || Wire.PublicKey(item.Sign) is not { } sign || Wire.PublicKey(item.Dh) is not { } dh)
        {
            return;
        }
        var epoch = _store.Epoch;
        if (_members.EpochsOf(item.Device)?.Removed >= epoch)
        {
            await _relaySync.FinishRotationAsync(_keys, cancel).ConfigureAwait(false);   // removed at this epoch: back only at a newer one
            return;
        }
        if (approving.Epoch != epoch || approving.Body is null)
        {
            if (Approval.Seal(_keys, dh, householdId, epoch, key, _members.Entries(compact: true)) is not { } body) return;
            approving = approving with { Epoch = epoch, Body = body };
            _store.Approving = approving;
        }
        var result = await _environment.Relay.ApproveAsync(_keys, householdId, item.Device, epoch, approving.Body!, cancel).ConfigureAwait(false);
        if (result.Ok)
        {
            _store.Approving = null;
            _members.Add(new MemberInfo(item.Device, NewPcName, ChassisKind.Desktop, sign, dh), epoch, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            Volatile.Write(ref _waitingApprovals, Math.Max(0, Volatile.Read(ref _waitingApprovals) - 1));
            _log.LogInformation("Approved {Device} into the household", item.Device);
            Info("The PC signed in as you is now in your household.");
            Kick();
            return;
        }
        if (result.Status is 403 or 404)
        {
            _store.Approving = null;                                           // not this PC's to approve, or no longer waiting
            return;
        }
        _log.LogInformation("The approval of {Device} didn't go ({Status}: {Problem}); it goes again at a later turn", item.Device, result.Status,
            result.Problem);
        if (result.Status == 409)
        {
            _store.Approving = approving with { Epoch = null, Body = null };  // sealed again, at the key the household is at
            await _relaySync.CatchUpAsync(_keys, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>The user said not to let the PC in: its request is taken off the server.</summary>
    private async Task DenyAsync(string householdId, JoinRequestItem item)
    {
        var result = await _environment.Relay.DenyAsync(_keys, householdId, item.Device, _stopping.Token).ConfigureAwait(false);
        if (result.Ok || result.Status is 404 or 409)
        {
            Volatile.Write(ref _waitingApprovals, Math.Max(0, Volatile.Read(ref _waitingApprovals) - 1));
        }
        else
        {
            _log.LogInformation("Turning {Device} away didn't reach the server ({Problem})", item.Device, result.Problem);
        }
    }

    private static string Commit(string nonce) => Wire.Decode(nonce) is { } bytes ? Wire.Encode(HouseholdCrypto.Commitment(bytes)) : "";

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    private bool MayStartApproval() => _store.ApprovalsStarted is not { } started || started.Day != Today || started.Count < ApprovalsADay;

    private void StartedApproval()
    {
        var today = Today;
        _store.ApprovalsStarted = (today, _store.ApprovalsStarted is { } started && started.Day == today ? started.Count + 1 : 1);
    }

    /// <summary>
    /// Links the account to this PC's household again every few hours, and at the first turn after signing in: the link is
    /// gone after some changes on the server, as when the household ended. With the recovery code's key, the recovery is
    /// put again with it, at the current key.
    /// </summary>
    private async Task LinkAgainIfDueAsync(string householdId, CancellationToken cancel)
    {
        if (_store.Session is not { } session) return;
        var now = _clock.GetUtcNow();
        if (_linkedAt is { } linked && now - linked < RelaySync.MembersEvery) return;
        var link = await _environment.Relay.LinkAsync(_keys, session, householdId, cancel).ConfigureAwait(false);
        if (link.Status == 401)
        {
            _store.Session = null;                                             // the session has ended
            return;
        }
        _linkedAt = now;                                                       // linked, or linked elsewhere: looked at again later
        if (link.Ok && _store.RecoveryKey is not null) _store.AddPending(new PendingOp(PendingOp.RecoveryEnvelope, householdId));
        if (link.Ok) await CheckRecoveryAsync(session, householdId, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// A PC signed in and waiting to join follows its request with each turn (plan 0.9). It answers the member that commits
    /// to approving it with a nonce of its own, once per request. Once that member reveals the nonce it committed to, and it
    /// matches, this PC's user is asked at once whether the other PC shows the same code, before anything is sealed. Once its
    /// user said so, and the approval is there, sealed by that member for the epoch the server names, this PC enters, and
    /// takes its request off the server. A request refused or lapsed lets the user ask again: this PC never asks by itself.
    /// </summary>
    private async Task CheckApprovedAsync(CancellationToken cancel)
    {
        if (_store.AskedToJoin is not { } householdId || _store.Session is not { } session) return;
        var listed = await _environment.Relay.OwnRequestsAsync(_keys, session, cancel).ConfigureAwait(false);
        if (listed.Status == 401)
        {
            _store.Session = null;                                             // the session has ended
            return;
        }
        if (!listed.Ok) return;
        if (listed.Value!.Requests?.FirstOrDefault(own => own.Household == householdId && own.Device == _keys.DeviceId) is not { } request)
        {
            EndRequest(canAskAgain: true);
            Info("Your request to join your household ended without an approval. You can ask again.");
            return;
        }
        if (request.Approver is not { } approver || request.Commit is null) return;          // no member has committed yet
        var answering = _store.Answering is { } kept && kept.Household == householdId ? kept : null;
        if (answering is null)
        {
            if (Wire.PublicKey(approver.Sign) is not { } sign || HouseholdCrypto.DeviceIdOf(sign) != approver.Device || Wire.PublicKey(approver.Dh) is null)
            {
                return;
            }
            answering = new Answering(householdId, approver.Device, approver.Sign, approver.Dh, request.Commit, Wire.Encode(HouseholdCrypto.NewNonce()));
            _store.Answering = answering;                                      // kept before it goes: one nonce per request
        }
        else if (answering.Approver != approver.Device || answering.Sign != approver.Sign || answering.Dh != approver.Dh || answering.Commit != request.Commit)
        {
            return;                                                            // never an answer to a second commit
        }
        if (request.Reveal is null)
        {
            await _environment.Relay.SendNonceAsync(_keys, session, answering.Nonce, cancel).ConfigureAwait(false);
            return;
        }
        if (Wire.Decode(request.Reveal) is not { Length: 32 } reveal
            || !CryptographicOperations.FixedTimeEquals(HouseholdCrypto.Commitment(reveal), Wire.Decode(answering.Commit)))
        {
            await RefuseJoinAsync(householdId, session, "This PC didn't join: the approving PC's code didn't hold together. You can ask again.", cancel)
                .ConfigureAwait(false);
            return;
        }
        if (!answering.Confirmed)
        {
            if (!_notices.AnyoneAtTheScreen || Interlocked.CompareExchange(ref _confirmingJoin, 1, 0) != 0) return;
            var code = HouseholdCrypto.ApprovalCode(_keys.SignPublic, _keys.DhPublic, Wire.Decode(answering.Sign)!, Wire.Decode(answering.Dh)!,
                Wire.Decode(answering.Nonce)!, reveal);
            Track(ConfirmJoinAsync(answering, code));
            return;
        }
        if (request.Approved is not { } approved) return;                     // the other PC's user hasn't approved yet
        var got = await _environment.Relay.GetKeyAsync(_keys, householdId, approved.Epoch, cancel).ConfigureAwait(false);
        if (!got.Ok) return;
        if (got.Value!.From != answering.Approver || got.Value.Epoch != approved.Epoch
            || Approval.Open(_keys, Wire.Decode(answering.Dh)!, got.Value.Body, householdId, approved.Epoch) is not { } sealedList)
        {
            await RefuseJoinAsync(householdId, session, "This PC didn't join: the approval didn't come from the PC whose code you checked. You can ask again.",
                cancel).ConfigureAwait(false);
            return;
        }
        EnterLocked(householdId, approved.Epoch, sealedList.Key, sealedList.Members, answering.Approver);
        _store.RelayConfirmed = true;
        _store.Answering = null;
        _store.CanAskAgain = false;
        _log.LogInformation("Approved into the household at epoch {Epoch}", approved.Epoch);
        Info("This PC joined your household.");
        await _environment.Relay.WithdrawAsync(_keys, session, cancel).ConfigureAwait(false);   // once, now that it is in
        Kick();
    }

    /// <summary>Asks this PC's user whether the approving PC shows the same code (plan 0.9). Codes match lets this PC enter once
    /// the approval is there; They don't match takes its request off the server, or takes it out if it was added already.</summary>
    private async Task ConfirmJoinAsync(Answering answering, string code)
    {
        try
        {
            var answer = await _prompts.ConfirmJoinAsync(code, _stopping.Token).ConfigureAwait(false);
            if (answer is null) return;                                         // unanswered: asked again at a later turn
            using (await EnterGateAsync(_stopping.Token, PairingGateWait).ConfigureAwait(false))
            {
                if (_store.Answering != answering || _store.AskedToJoin != answering.Household) return;
                if (answer == true) _store.Answering = answering with { Confirmed = true };
                else if (_store.Session is { } session)
                {
                    await RefuseJoinAsync(answering.Household, session, "This PC didn't join: the codes didn't match. You can ask again.", _stopping.Token)
                        .ConfigureAwait(false);
                }
            }
            Kick();
        }
        finally
        {
            Interlocked.Exchange(ref _confirmingJoin, 0);
            Publish();
        }
    }

    /// <summary>This PC won't join: its request goes from the server, and so does this PC if it was added already (plan 0.9).</summary>
    private async Task RefuseJoinAsync(string householdId, string session, string text, CancellationToken cancel)
    {
        _log.LogWarning("Not joining the household: {Reason}", text);
        _store.AddPending(new PendingOp(PendingOp.Remove, householdId, Device: _keys.DeviceId));   // dropped at once when it wasn't added
        EndRequest(canAskAgain: true);
        Info(text);
        await _environment.Relay.WithdrawAsync(_keys, session, cancel).ConfigureAwait(false);
        Kick();
    }

    private void EndRequest(bool canAskAgain)
    {
        _store.AskedToJoin = null;
        _store.Answering = null;
        _store.CanAskAgain = canAskAgain;
        Publish();
    }

    /// <summary>N2: asks the household again to let this PC in, as only its user may (plan 0.9).</summary>
    private async Task<PipeMessage> AskAgainAsync(AskAgainRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        if (_store.Session is not { } session) return Reply(request.Id, false, "Sign in first to ask to join your household.");
        if (_store.HouseholdId is not null) return Reply(request.Id, false, AskedAgainButIn);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(AccountWait);
        try
        {
            var ask = await _environment.Relay.AskToJoinAsync(_keys, session, deadline.Token).ConfigureAwait(false);
            if (ask.Status == 401)
            {
                _store.Session = null;
                return Reply(request.Id, false, "Your session has ended. Sign in again to ask to join your household.");
            }
            if (!ask.Ok || ask.Value!.HouseholdId is not { } householdId) return Reply(request.Id, false, $"Couldn't ask to join your household: {ask.Problem}.");
            _store.AskedToJoin = householdId;
            _store.Answering = null;
            _store.CanAskAgain = false;
            return Reply(request.Id, true, WaitingForApproval);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Reply(request.Id, false, "The server didn't answer in time. Try again.");
        }
    }

    private async Task<PipeMessage> SignOutAsync(SignOutRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        if (_store.Session is not { } session) return Reply(request.Id, true, "This PC isn't signed in.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(AccountWait);
        var result = await _environment.Relay.SignOutAsync(_keys, session, deadline.Token).ConfigureAwait(false);
        if (!result.Ok && result.Status != 401) return Reply(request.Id, false, $"Couldn't sign out: {result.Problem}.");
        _store.Session = null;
        _store.Account = null;
        EndRequest(canAskAgain: false);
        ForgetRecovery();                                                      // this PC's recovery key goes with the account
        return Reply(request.Id, true, "Signed out.");
    }

    /// <summary>Deletes the account on the server: its link, sessions, requests and recovery. The household and its members
    /// carry on without sign-in.</summary>
    private async Task<PipeMessage> DeleteAccountAsync(DeleteAccountRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        if (_store.Session is not { } session) return Reply(request.Id, false, "Sign in first to delete your account.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(AccountWait);
        var result = await _environment.Relay.DeleteAccountAsync(_keys, session, deadline.Token).ConfigureAwait(false);
        if (result.Status == 401)
        {
            _store.Session = null;
            return Reply(request.Id, false, "Your session has ended. Sign in again to delete your account.");
        }
        if (!result.Ok) return Reply(request.Id, false, $"Couldn't delete your account: {result.Problem}.");
        _store.Session = null;
        _store.Account = null;
        EndRequest(canAskAgain: false);
        ForgetRecovery();
        _log.LogInformation("Deleted the account; the household carries on without sign-in");
        return Reply(request.Id, true, "Your account was deleted. Your household carries on without sign-in.");
    }
}
