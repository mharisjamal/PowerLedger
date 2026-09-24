using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;

namespace PowerLedger.Service.Households;

/// <summary>N2's approvals (households design §7, plan C12), on the household worker: a PC signed in as the account joins
/// once a member approves it, the member sealing the household key to it, as with Signal's or WhatsApp's linked devices.</summary>
internal sealed partial class HouseholdWorker
{
    /// <summary>How many epochs a newly approved PC looks through for its envelope.</summary>
    private const int EpochsToLookThrough = 32;

    /// <summary>The PCs waiting to join that this PC's user has been asked about: true once approved, until the server has
    /// taken the approval.</summary>
    private readonly ConcurrentDictionary<string, bool> _asked = new(StringComparer.Ordinal);
    private int _waitingApprovals;

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

    private async Task AskToApproveAsync(string householdId, JoinRequestItem item)
    {
        if (!await _prompts.AskToApproveAsync(_stopping.Token).ConfigureAwait(false))
        {
            if (!_notices.AnyoneAtTheScreen) _asked.TryRemove(item.Device, out _);    // nobody saw it: asked again next time
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
                _log.LogInformation("Approved {Device} into the household", item.Device);
                Info("The PC signed in as you is now in your household.");
                Kick();
            }
            return;
        }
        _log.LogInformation("The approval of {Device} didn't go ({Status}: {Problem}); it goes again at the next turn", item.Device, result.Status,
            result.Problem);
    }

    /// <summary>A PC signed in and waiting to join looks, with each turn, whether it has been approved: once the server lists
    /// it as a member, it finds the envelope the approving member sealed for it and enters the household.</summary>
    private async Task CheckApprovedAsync(CancellationToken cancel)
    {
        if (_store.AskedToJoin is not { } householdId || _store.Session is null) return;
        var members = await _environment.Relay.MembersAsync(_keys, householdId, cancel).ConfigureAwait(false);
        if (!members.Ok) return;                                                // 403 while it waits
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
            EnterLocked(householdId, epoch, key, []);
            _store.RelayConfirmed = true;
            _log.LogInformation("Approved into the household at epoch {Epoch}", epoch);
            Info("This PC joined your household.");
            Kick();
            return;
        }
    }

    /// <summary>A new household key goes into the recovery envelope too, when this PC is signed in and holds the recovery
    /// code's key (households design §7): the server is told with the rest.</summary>
    private void QueueRecovery(string householdId, int epoch, byte[] key)
    {
        if (_store.Session is null || _store.RecoveryKey is not { } recoveryKey) return;
        _store.AddPending(new PendingOp(PendingOp.RecoveryEnvelope, householdId, Epoch: epoch, Recovery: Recovery.Envelope(recoveryKey, householdId, epoch, key)));
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
        _store.AskedToJoin = null;
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
        _store.AskedToJoin = null;
        _store.RecoveryKey = null;
        _log.LogInformation("Deleted the account; the household carries on without sign-in");
        return Reply(request.Id, true, "Your account was deleted. Your household carries on without sign-in.");
    }
}
