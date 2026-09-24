using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;

namespace PowerLedger.Service.Households;

/// <summary>
/// N2's recovery envelope (households design §7): the household key sealed under the key made from the recovery code, with
/// its household and epoch as associated data (plan 0.8), and the verifier, HKDF of the household key: the server keeps it
/// with the epoch it was put at, and a recovering PC shows it, which only a PC that opened the envelope can make, since the
/// server never has the key. It opens only at the household's current epoch, so a new key is put again.
/// </summary>
internal static class Recovery
{
    public static byte[] Aad(string householdId, int epoch) => Encoding.UTF8.GetBytes($"powerledger recovery|{householdId}|{epoch}");

    public static byte[] Verifier(byte[] householdKey) => HouseholdCrypto.Hkdf(householdKey, [], "powerledger recovery verifier");

    public static RecoveryBody Envelope(byte[] recoveryKey, string householdId, int epoch, byte[] householdKey) => new(
        Wire.Encode(HouseholdCrypto.Seal(recoveryKey, householdKey, Aad(householdId, epoch))), Wire.Encode(Verifier(householdKey)));

    /// <summary>The household key in the account's envelope, at the epoch the server keeps with it; null when the code's key
    /// doesn't open it there.</summary>
    public static byte[]? Open(byte[] recoveryKey, RecoveryReply reply)
    {
        if (reply is not { HouseholdId: { } householdId, Epoch: > 0 and var epoch } || Wire.Decode(reply.Body) is not { } sealedKey) return null;
        try
        {
            var key = HouseholdCrypto.Open(recoveryKey, sealedKey, Aad(householdId, epoch));
            return key.Length == HouseholdCrypto.KeyLength ? key : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>N2's sign-in (households design §7, plan C11 and 0.8), on the household worker.</summary>
internal sealed partial class HouseholdWorker
{
    internal const string WaitingForApproval = "Waiting for another PC in your household to approve this one.";

    internal const string LinkedElsewhere =
        "Your account is linked to another household. To join it, leave this PC's household first, then sign in again.";

    internal const string KeepTheCode =
        "Keep this recovery code somewhere safe. With it you can bring your household back on a new PC without another PC approving it.";

    /// <summary>How long signing in may take in all, well inside the pipe's limit.</summary>
    private static readonly TimeSpan AccountWait = TimeSpan.FromSeconds(7);

    /// <summary>True when the account's recovery envelope no longer opens at the household's current key, as this PC last
    /// saw it: the App offers a new code (plan 0.8).</summary>
    private bool _recoveryMissing;

    /// <summary>True once the App at the screen has been given the recovery code waiting to be shown.</summary>
    private bool _recoveryShown;

    /// <summary>
    /// Signs in with the ID token the App got from the provider, the nonce's salt with it (the token's nonce is SHA-256 of
    /// this PC's device ID and the salt, so it signs in only this PC), and keeps the session. Then, as the account stands:
    /// with a recovery code, this PC takes the account's household back without approval; with a household this PC isn't
    /// in, it asks to join, unless this PC is in another household, which it would have to leave first; with none and this
    /// PC in one, the account is linked to it, and a recovery code made and shown once as a notice.
    /// </summary>
    private async Task<PipeMessage> SignInAsync(SignInRequest request, CancellationToken cancel)
    {
        if (request.Provider is not ("microsoft" or "google")) return Reply(request.Id, false, "Sign in with Microsoft or Google.");
        if (request.IdToken is not { Length: > 0 and <= 16_384 } || request.Nonce is not { Length: > 0 and <= 256 })
        {
            return Reply(request.Id, false, "The sign-in didn't come back complete. Try again.");
        }
        string? recovery = null;
        if (request.RecoveryCode is { } typed && (recovery = RecoveryCode.Normalize(typed)) is null)
        {
            return Reply(request.Id, false, "That isn't a recovery code. A recovery code has 24 letters and digits.");
        }

        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(AccountWait);
        try
        {
            var signIn = await _environment.Relay.SignInAsync(_keys, request.Provider, request.IdToken, request.Nonce, deadline.Token)
                .ConfigureAwait(false);
            if (!signIn.Ok) return Reply(request.Id, false, $"Couldn't sign in: {(signIn.Status is 400 or 401 or 403 && signIn.Error is { } why ? why : signIn.Problem)}.");
            var account = signIn.Value!;
            if (_store.Account is { } before && before != account.Account) ForgetRecovery();   // another account: its code isn't this one's
            _store.Session = account.Session;
            _store.Account = account.Account;
            _linkedAt = null;                                                  // links again at the next turn, as a check
            _log.LogInformation("Signed in with {Provider}", request.Provider);

            if (recovery is not null) return await RecoverLockedAsync(request.Id, account.Session, recovery, deadline.Token).ConfigureAwait(false);
            if (account.HouseholdId is { } linked)
            {
                if (_store.HouseholdId == linked)
                {
                    _linkedAt = _clock.GetUtcNow();
                    if (!account.HasRecovery) await MakeRecoveryAsync(account.Session, linked, deadline.Token).ConfigureAwait(false);
                    else await CheckRecoveryAsync(account.Session, linked, deadline.Token).ConfigureAwait(false);
                    return Reply(request.Id, true, "Signed in.");
                }
                if (_store.HouseholdId is not null)
                {
                    // In a household already: joining the account's would leave it, which signing in mustn't do on its own.
                    await _environment.Relay.SignOutAsync(_keys, account.Session, deadline.Token).ConfigureAwait(false);
                    _store.Session = null;
                    _store.Account = null;
                    return Reply(request.Id, false, LinkedElsewhere);
                }
                var ask = await _environment.Relay.AskToJoinAsync(_keys, account.Session, deadline.Token).ConfigureAwait(false);
                if (!ask.Ok) return Reply(request.Id, false, $"Signed in, but couldn't ask to join your household: {ask.Problem}.");
                _store.AskedToJoin = linked;
                return Reply(request.Id, true, WaitingForApproval);
            }
            if (_store.HouseholdId is { } mine)
            {
                var link = await _environment.Relay.LinkAsync(_keys, account.Session, mine, deadline.Token).ConfigureAwait(false);
                if (!link.Ok) return Reply(request.Id, true, $"Signed in, but your household couldn't be linked to your account: {link.Problem}.");
                _linkedAt = _clock.GetUtcNow();
                await MakeRecoveryAsync(account.Session, mine, deadline.Token).ConfigureAwait(false);
                return Reply(request.Id, true, "Signed in, and your household is linked to your account.");
            }
            return Reply(request.Id, true, "Signed in.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Reply(request.Id, false, "The server didn't answer in time. Try again.");
        }
    }

    /// <summary>Makes a new recovery code for the linked household on request (plan 0.8), in place of any before it; it comes
    /// as a notice.</summary>
    private async Task<PipeMessage> NewRecoveryCodeAsync(NewRecoveryCodeRequest request, CancellationToken cancel)
    {
        using var entered = await EnterGateAsync(cancel).ConfigureAwait(false);
        if (_store.Session is not { } session) return Reply(request.Id, false, "Sign in first to make a recovery code.");
        if (_store.HouseholdId is not { } householdId) return Reply(request.Id, false, NotInOne);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(AccountWait);
        try
        {
            var link = await _environment.Relay.LinkAsync(_keys, session, householdId, deadline.Token).ConfigureAwait(false);
            if (!link.Ok) return Reply(request.Id, false, $"Your household couldn't be linked to your account: {link.Problem}.");
            await _relaySync.CatchUpAsync(_keys, deadline.Token).ConfigureAwait(false);   // sealed at the key the server is at
            return await MakeRecoveryAsync(session, householdId, deadline.Token).ConfigureAwait(false)
                ? Reply(request.Id, true, "A new recovery code was made.")
                : Reply(request.Id, false, "Couldn't make a new recovery code. Check this PC is online and try again.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Reply(request.Id, false, "The server didn't answer in time. Try again.");
        }
    }

    /// <summary>Makes the recovery code for the household, puts the envelope with the account, keeps the code's key, and shows
    /// the code once as a <see cref="NoticeKind.RecoveryCode"/> notice, the code kept encrypted until the App says it was seen.</summary>
    /// <returns>False when the envelope couldn't be put.</returns>
    private async Task<bool> MakeRecoveryAsync(string session, string householdId, CancellationToken cancel)
    {
        if (_store.CurrentKey is not { } key) return false;
        var code = RecoveryCode.New();
        var recoveryKey = RecoveryCode.Key(RecoveryCode.Normalize(code)!);
        var put = await _environment.Relay.PutRecoveryAsync(_keys, session, Recovery.Envelope(recoveryKey, householdId, _store.Epoch, key), cancel)
            .ConfigureAwait(false);
        if (!put.Ok)
        {
            _log.LogWarning("The recovery envelope couldn't be kept with the account ({Problem})", put.Problem);
            return false;
        }
        _store.RecoveryKey = recoveryKey;
        _recoveryMissing = false;
        _store.RecoveryCodeToShow = (Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), code);
        _recoveryShown = false;
        ShowRecoveryCode();
        return true;
    }

    /// <summary>Gives the App at the screen the recovery code waiting to be shown, once each time the service runs.</summary>
    private void ShowRecoveryCode()
    {
        if (_recoveryShown || _store.RecoveryCodeToShow is not { } waiting) return;
        _recoveryShown = _notices.Publish(new HouseholdNotice(NoticeKind.RecoveryCode, waiting.PromptId, KeepTheCode, null, null, null, waiting.Code));
    }

    /// <summary>The App's answer to the recovery code's notice: it was seen, so the code is forgotten.</summary>
    /// <returns>False when no code waits under that prompt.</returns>
    private bool RecoveryCodeSeen(string promptId)
    {
        if (_store.RecoveryCodeToShow is not { } waiting || waiting.PromptId != promptId) return false;
        _store.RecoveryCodeToShow = null;
        return true;
    }

    /// <summary>Forgets this PC's recovery code and its key, as on signing out or signing in as another account.</summary>
    private void ForgetRecovery()
    {
        _store.RecoveryKey = null;
        _store.RecoveryCodeToShow = null;
        _recoveryMissing = false;
    }

    /// <summary>Whether the account's recovery envelope still opens at the household's current key: a PC holding the code's
    /// key puts it again with every new key, but with none here, the one the server keeps may be for an older key (plan 0.8).</summary>
    private async Task CheckRecoveryAsync(string session, string householdId, CancellationToken cancel)
    {
        if (_store.RecoveryKey is not null)
        {
            _recoveryMissing = false;
            return;
        }
        var got = await _environment.Relay.GetRecoveryAsync(_keys, session, cancel).ConfigureAwait(false);
        if (got.Status == 404) _recoveryMissing = true;
        else if (got.Ok) _recoveryMissing = got.Value!.HouseholdId != householdId || got.Value.Epoch != _store.Epoch;
    }

    /// <summary>Takes the account's household back with the recovery code: opens the envelope, proves it to the server, and
    /// enters the household with no approval (households design §7).</summary>
    private async Task<PipeMessage> RecoverLockedAsync(long id, string session, string recovery, CancellationToken cancel)
    {
        var got = await _environment.Relay.GetRecoveryAsync(_keys, session, cancel).ConfigureAwait(false);
        if (got.Status == 404) return Reply(id, false, "Your account has no household to recover.");
        if (!got.Ok) return Reply(id, false, $"Couldn't recover your household: {got.Problem}.");
        var recoveryKey = RecoveryCode.Key(recovery);
        if (Recovery.Open(recoveryKey, got.Value!) is not { } key) return Reply(id, false, "That recovery code doesn't open your account's household.");
        var envelope = got.Value!;
        var recovered = await _environment.Relay.RecoverAsync(_keys, session, Recovery.Verifier(key), cancel).ConfigureAwait(false);
        if (!recovered.Ok) return Reply(id, false, $"Couldn't recover your household: {recovered.Problem}.");
        EnterLocked(envelope.HouseholdId!, envelope.Epoch!.Value, key, [], null);
        _store.RecoveryKey = recoveryKey;
        _store.RelayConfirmed = true;
        _log.LogInformation("Recovered the household with the recovery code");
        Kick();
        return Reply(id, true, "Your household is back on this PC.");
    }
}
