using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>N2's approvals (households design §7, plan C12), on the household worker: a PC signed in as the account joins
/// once a member approves it, the member sealing the household key to it, as with Signal's or WhatsApp's linked devices.</summary>
internal sealed partial class HouseholdWorker
{
    /// <summary>How many epochs a newly approved PC looks through for its envelope.</summary>
    private const int EpochsToLookThrough = 32;

    /// <summary>What an approved PC is called here until its first batch gives its name.</summary>
    internal const string NewPcName = "New PC";

    /// <summary>The PCs waiting to join that this PC's user has been asked about: true once approved, until the server has
    /// taken the approval.</summary>
    private readonly ConcurrentDictionary<string, bool> _asked = new(StringComparer.Ordinal);
    private int _waitingApprovals;

    /// <summary>1 while this PC's user is asked to check the approval code, so it isn't asked twice.</summary>
    private int _confirmingJoin;

    /// <summary>When this PC last linked the account to its household, as a check that the link is still there.</summary>
    private DateTimeOffset? _linkedAt;

    /// <summary>With its sync, a member signed in looks at the PCs waiting to join and asks the user at the screen about each
    /// new one; with nobody at the screen to ask, it doesn't look. An approval the server refused, as when this PC's key was
    /// behind the household's, goes again.</summary>
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
        var waiting = result.Value!
            .Where(item => Wire.IsDeviceId(item.Device) && Wire.PublicKey(item.Sign) is { } sign && HouseholdCrypto.DeviceIdOf(sign) == item.Device
                && Wire.PublicKey(item.Dh) is not null && item.Device != _keys.DeviceId)
            .Take(Wire.MaxMembers)
            .ToList();
        Volatile.Write(ref _waitingApprovals, waiting.Count);
        foreach (var item in waiting)
        {
            if (_asked.TryGetValue(item.Device, out var approved))
            {
                if (approved) await ApproveLockedAsync(householdId, item, cancel).ConfigureAwait(false);
                continue;
            }
            _asked[item.Device] = false;
            Track(AskToApproveAsync(householdId, item));
        }
    }

    /// <summary>Asks the user whether to let the waiting PC in, showing the approval code over its keys and this PC's
    /// key-agreement key, which the PC asking shows too once approved (plan 0.8).</summary>
    private async Task AskToApproveAsync(string householdId, JoinRequestItem item)
    {
        var asYou = item.Account is not null && item.Account == _store.Account;
        var code = HouseholdCrypto.ApprovalCode(Wire.PublicKey(item.Sign)!, Wire.PublicKey(item.Dh)!, _keys.DhPublic);
        if (!await _prompts.AskToApproveAsync(asYou, code, _stopping.Token).ConfigureAwait(false))
        {
            if (!_notices.AnyoneAtTheScreen)
            {
                _asked.TryRemove(item.Device, out _);                          // nobody saw it: asked again next time
                return;
            }
            await DenyAsync(householdId, item).ConfigureAwait(false);
            return;
        }
        _asked[item.Device] = true;
        using (await EnterGateAsync(_stopping.Token, PairingGateWait).ConfigureAwait(false))
        {
            if (_store.HouseholdId == householdId) await ApproveLockedAsync(householdId, item, _stopping.Token).ConfigureAwait(false);
        }
        Publish();
    }

    /// <summary>Seals the current household key to the waiting PC and posts the approval, which makes it a member.</summary>
    private async Task ApproveLockedAsync(string householdId, JoinRequestItem item, CancellationToken cancel)
    {
        if (_store.CurrentKey is not { } key || Wire.PublicKey(item.Dh) is not { } dh) return;
        var epoch = _store.Epoch;
        var envelope = Wire.Encode(HouseholdCrypto.WrapFor(_keys.Dh, dh, key, KeyWrap.Context(householdId, epoch)));
        var result = await _environment.Relay.ApproveAsync(_keys, householdId, item.Device, epoch, envelope, cancel).ConfigureAwait(false);
        if (result.Ok || result.Status == 404)
        {
            _asked.TryRemove(item.Device, out _);
            Volatile.Write(ref _waitingApprovals, Math.Max(0, Volatile.Read(ref _waitingApprovals) - 1));
            if (result.Ok)
            {
                // A member now, added at this PC's epoch and known here by the keys its user's approval vouched for: its batches
                // check against them, and this PC's own member list introduces it to the others (plan 0.9). Its name comes with
                // its first batch.
                _members.Add(new MemberInfo(item.Device, NewPcName, ChassisKind.Desktop, Wire.PublicKey(item.Sign)!, dh), epoch,
                    _clock.GetUtcNow().ToUnixTimeMilliseconds());
                _log.LogInformation("Approved {Device} into the household", item.Device);
                Info("The PC signed in as you is now in your household.");
                Kick();
            }
            return;
        }
        _log.LogInformation("The approval of {Device} didn't go ({Status}: {Problem}); it goes again at the next turn", item.Device, result.Status,
            result.Problem);
        if (result.Status == 409) await _relaySync.CatchUpAsync(_keys, cancel).ConfigureAwait(false);    // sealed an older key
    }

    /// <summary>The user said not to let the PC in: its request is taken off the server, so no member is asked again.</summary>
    private async Task DenyAsync(string householdId, JoinRequestItem item)
    {
        var result = await _environment.Relay.DenyAsync(_keys, householdId, item.Device, _stopping.Token).ConfigureAwait(false);
        if (result.Ok || result.Status == 404)
        {
            Volatile.Write(ref _waitingApprovals, Math.Max(0, Volatile.Read(ref _waitingApprovals) - 1));
            Publish();
        }
        else
        {
            _log.LogInformation("Turning {Device} away didn't reach the server ({Problem})", item.Device, result.Problem);
        }
    }

    /// <summary>
    /// Links the account to this PC's household again every few hours, and at the first turn after signing in: the link is
    /// gone after some changes on the server, as when the PC that made it is removed (the lead's Worker contract, D). With
    /// the recovery code's key, the recovery envelope is put again with it, at the current key.
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
    /// A PC signed in and waiting to join looks, with each turn, whether it has been approved: once the server lists it as a
    /// member, it finds the envelope the approving member sealed for it. It joins only once its user has checked the
    /// approving PC showed the approval code this PC works out from the key that envelope opens with (plan 0.8); with
    /// nobody at the screen to ask, it asks at a later turn.
    /// </summary>
    private async Task CheckApprovedAsync(CancellationToken cancel)
    {
        if (_store.AskedToJoin is not { } householdId || _store.Session is null || Volatile.Read(ref _confirmingJoin) != 0) return;
        if (!_notices.AnyoneAtTheScreen) return;
        var members = await _environment.Relay.MembersAsync(_keys, householdId, cancel).ConfigureAwait(false);
        if (!members.Ok) return;                                                // 401 while it waits
        for (var epoch = 1; epoch <= EpochsToLookThrough; epoch++)
        {
            var got = await _environment.Relay.GetKeyAsync(_keys, householdId, epoch, cancel).ConfigureAwait(false);
            if (got.Status == 404) continue;
            if (!got.Ok) return;
            var sealer = members.Value!.FirstOrDefault(member => member.Device == got.Value!.From);
            if (Wire.PublicKey(sealer?.Dh) is not { } sealerDh || KeyWrap.Open(_keys, sealerDh, got.Value!.Body, householdId, epoch) is not { } key)
            {
                _log.LogWarning("The envelope sealed for this PC at epoch {Epoch} didn't open", epoch);
                return;
            }
            Interlocked.Exchange(ref _confirmingJoin, 1);
            Track(ConfirmJoinAsync(householdId, epoch, key, sealerDh));
            return;
        }
    }

    /// <summary>Asks this PC's user to check the approval code, and joins on yes. On no, the codes differed: the keys came from
    /// someone else, so this PC doesn't join and takes itself off the server's list.</summary>
    private async Task ConfirmJoinAsync(string householdId, int epoch, byte[] key, byte[] sealerDh)
    {
        try
        {
            var code = HouseholdCrypto.ApprovalCode(_keys.SignPublic, _keys.DhPublic, sealerDh);
            var yes = await _prompts.ConfirmJoinAsync(code, _stopping.Token).ConfigureAwait(false);
            using (await EnterGateAsync(_stopping.Token, PairingGateWait).ConfigureAwait(false))
            {
                if (_store.AskedToJoin != householdId) return;
                if (yes)
                {
                    EnterLocked(householdId, epoch, key, [], null);
                    _store.RelayConfirmed = true;
                    _log.LogInformation("Approved into the household at epoch {Epoch}", epoch);
                    Info("This PC joined your household.");
                }
                else if (_notices.AnyoneAtTheScreen)
                {
                    _store.AskedToJoin = null;
                    _store.AddPending(new PendingOp(PendingOp.Remove, householdId, Device: _keys.DeviceId));
                    _log.LogWarning("The approval code didn't match, so this PC didn't join");
                    Info("This PC didn't join: the codes didn't match. Sign in again to ask once more.");
                }
            }
            Publish();
            Kick();
        }
        finally
        {
            Interlocked.Exchange(ref _confirmingJoin, 0);
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
        _store.AskedToJoin = null;
        ForgetRecovery();                                                      // this PC's recovery key goes with the account
        return Reply(request.Id, true, "Signed out.");
    }

    /// <summary>Deletes the account on the server: its link, sessions, requests and recovery envelope. The household and its
    /// members carry on without sign-in.</summary>
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
        _store.AskedToJoin = null;
        ForgetRecovery();
        _log.LogInformation("Deleted the account; the household carries on without sign-in");
        return Reply(request.Id, true, "Your account was deleted. Your household carries on without sign-in.");
    }
}
