using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Relay;

namespace PowerLedger.Service.Households;

/// <summary>
/// N2's recovery (households design §7, plan 0.9): the household key and the member list, sealed under the key made from the
/// recovery code with the household and epoch as associated data, and the verifier, made from the code's key alone: the
/// server keeps its hash, and a recovering PC shows it, which only a PC given the code can make.
/// </summary>
internal static class Recovery
{
    public static byte[] Aad(string householdId, int epoch) => Encoding.UTF8.GetBytes($"powerledger recovery|{householdId}|{epoch}");

    public static byte[] Verifier(byte[] codeKey) => HouseholdCrypto.Hkdf(codeKey, [], "powerledger recovery verifier");

    /// <summary>The recovery as put, sealed at <paramref name="epoch"/>; null when even the current members alone don't fit.</summary>
    public static RecoveryBody? Envelope(byte[] codeKey, string householdId, int epoch, byte[] householdKey, List<WireMember> members, bool replace) =>
        KeyLists.Seal(householdKey, members, plain => HouseholdCrypto.Seal(codeKey, plain, Aad(householdId, epoch))) is { } body
            ? new RecoveryBody(body, Wire.Encode(Verifier(codeKey)), epoch, replace)
            : null;

    /// <summary>The key and member list in the account's recovery, at the epoch it was sealed at; null when the code's key
    /// doesn't open it.</summary>
    public static (byte[] Key, List<WireMember> Members)? Open(byte[] codeKey, string householdId, RecoveryReply reply)
    {
        if (reply.Epoch <= 0 || Wire.Decode(reply.Body) is not { } sealedBody) return null;
        try
        {
            return KeyLists.Read(HouseholdCrypto.Open(codeKey, sealedBody, Aad(householdId, reply.Epoch)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>N2's sign-in (households design §7, plan C11, 0.8 and 0.9), on the household worker.</summary>
internal sealed partial class HouseholdWorker
{
    internal const string WaitingForApproval = "Waiting for another PC in your household to approve this one.";

    internal const string LinkedElsewhere =
        "Your account is linked to another household. To join it, leave this PC's household first, then sign in again.";

    internal const string KeepTheCode =
        "Keep this recovery code somewhere safe. With it you can bring your household back on a new PC without another PC approving it.";

    /// <summary>How long signing in may take in all, well inside the pipe's limit.</summary>
    private static readonly TimeSpan AccountWait = TimeSpan.FromSeconds(7);

    /// <summary>True when, as this PC last saw it, the account has no recovery, or one whose holder isn't a current member:
    /// the App offers a new code (plan 0.9).</summary>
    private bool _recoveryMissing;

    /// <summary>True once the App at the screen has been given the recovery code waiting to be shown.</summary>
    private bool _recoveryShown;

    /// <summary>
    /// Signs in with the ID token the App got from the provider, the nonce's salt with it (the token's nonce is SHA-256 of
    /// this PC's device ID and the salt, so it signs in only this PC), and keeps the session. Then, as the account stands:
    /// with a recovery code, this PC takes the account's household back without approval, unless it is in another household,
    /// which it would have to leave first; with a household this PC isn't in, it asks to join, unless it is in another; with
    /// none and this PC in one, the account is linked to it, and a recovery code made and shown once as a notice.
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

            if (account.HouseholdId is { } linked && _store.HouseholdId is { } mine && mine != linked)
            {
                // In another household: joining or recovering the account's would take this PC out of it, which signing in
                // mustn't do on its own (plan 0.9).
                await _environment.Relay.SignOutAsync(_keys, account.Session, deadline.Token).ConfigureAwait(false);
                _store.Session = null;
                _store.Account = null;
                return Reply(request.Id, false, LinkedElsewhere);
            }
            if (recovery is not null)
            {
                return account.HouseholdId is { } recoverable
                    ? await RecoverLockedAsync(request.Id, account.Session, recoverable, recovery, deadline.Token).ConfigureAwait(false)
                    : Reply(request.Id, false, "Your account has no household to recover.");
            }
            if (account.HouseholdId is { } asked)
            {
                if (_store.HouseholdId == asked)
                {
                    _linkedAt = _clock.GetUtcNow();
                    if (!account.HasRecovery) await MakeRecoveryAsync(account.Session, asked, deadline.Token).ConfigureAwait(false);
                    else await CheckRecoveryAsync(account.Session, asked, deadline.Token).ConfigureAwait(false);
                    return Reply(request.Id, true, "Signed in.");
                }
                var ask = await _environment.Relay.AskToJoinAsync(_keys, account.Session, deadline.Token).ConfigureAwait(false);
                if (!ask.Ok) return Reply(request.Id, false, $"Signed in, but couldn't ask to join your household: {ask.Problem}.");
                _store.AskedToJoin = asked;                                    // a new request: its approval starts afresh (plan 0.9)
                _store.Answering = null;
                _store.CanAskAgain = false;
                return Reply(request.Id, true, WaitingForApproval);
            }
            if (_store.HouseholdId is { } household)
            {
                var link = await _environment.Relay.LinkAsync(_keys, account.Session, household, deadline.Token).ConfigureAwait(false);
                if (!link.Ok) return Reply(request.Id, true, $"Signed in, but your household couldn't be linked to your account: {link.Problem}.");
                _linkedAt = _clock.GetUtcNow();
                await MakeRecoveryAsync(account.Session, household, deadline.Token).ConfigureAwait(false);
                return Reply(request.Id, true, "Signed in, and your household is linked to your account.");
            }
            return Reply(request.Id, true, "Signed in.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Reply(request.Id, false, "The server didn't answer in time. Try again.");
        }
    }

    /// <summary>Makes a new recovery code for the linked household on request (plan 0.9), in place of any before it, whichever
    /// PC held that one; it comes as a notice.</summary>
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

    /// <summary>Makes a new recovery code for the household (plan 0.9): the key and member list sealed under the code's key at
    /// the current epoch, put in place of any other code, which makes this PC the one holding it. This PC keeps the code's
    /// key, to put the recovery again with each new key, and shows the code once as a <see cref="NoticeKind.RecoveryCode"/>
    /// notice, kept encrypted until the App says it was seen.</summary>
    /// <returns>False when the recovery couldn't be put.</returns>
    private async Task<bool> MakeRecoveryAsync(string session, string householdId, CancellationToken cancel)
    {
        var code = RecoveryCode.New();
        var codeKey = RecoveryCode.Key(RecoveryCode.Normalize(code)!);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_store.CurrentKey is not { } key
                || Recovery.Envelope(codeKey, householdId, _store.Epoch, key, _members.Entries(compact: true), replace: true) is not { } body)
            {
                return false;
            }
            var put = await _environment.Relay.PutRecoveryAsync(_keys, session, body, cancel).ConfigureAwait(false);
            if (put.Ok)
            {
                _store.RecoveryKey = codeKey;
                _recoveryMissing = false;
                ShowNewRecoveryCode(code);
                return true;
            }
            _log.LogWarning("The recovery couldn't be kept with the account ({Problem})", put.Problem);
            if (put.Status != 409) return false;
            await _relaySync.CatchUpAsync(_keys, cancel).ConfigureAwait(false);   // the key moved on: sealed again at the one the server is at
        }
        return false;
    }

    private void ShowNewRecoveryCode(string code)
    {
        _store.RecoveryCodeToShow = (Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), code);
        _recoveryShown = false;
        ShowRecoveryCode();
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

    /// <summary>
    /// Whether the account's recovery still works (plan 0.9): it is missing when the server has none, or its holder isn't a
    /// current member here. A PC holding a code the server no longer has, or that a newer code replaced, forgets it, unless
    /// its own new code still waits to go.
    /// </summary>
    private async Task CheckRecoveryAsync(string session, string householdId, CancellationToken cancel)
    {
        var got = await _environment.Relay.GetRecoveryAsync(_keys, session, cancel).ConfigureAwait(false);
        if (got.Status != 404 && !got.Ok) return;
        var holder = got.Value?.Holder;
        _recoveryMissing = holder is null || (holder != _keys.DeviceId && _members.Current(holder) is null);
        var waiting = _store.Pending.Any(op => op.Kind == PendingOp.RecoveryEnvelope && op.Household == householdId && op.Replace == true);
        if (_store.RecoveryKey is not null && holder != _keys.DeviceId && !waiting)
        {
            _log.LogInformation("The account's recovery has a newer code, or none; this PC forgets its own");
            _store.RecoveryKey = null;
            _store.RecoveryCodeToShow = null;
        }
    }

    /// <summary>
    /// Takes the account's household back with the recovery code (households design §7, plan 0.9): opens the recovery with
    /// the code's key, at whatever epoch it was sealed, and proves the code to the server, which makes this PC the household's
    /// only current member, removing every other at the household's current epoch. The code is used up: this PC doesn't keep
    /// it, makes a new key without the others, and a new code, which goes to the server once the key has.
    /// </summary>
    private async Task<PipeMessage> RecoverLockedAsync(long id, string session, string householdId, string code, CancellationToken cancel)
    {
        var codeKey = RecoveryCode.Key(code);
        var got = await _environment.Relay.GetRecoveryAsync(_keys, session, cancel).ConfigureAwait(false);
        if (got.Status == 404) return Reply(id, false, "Your account has no household to recover.");
        if (!got.Ok) return Reply(id, false, $"Couldn't recover your household: {got.Problem}.");
        if (Recovery.Open(codeKey, householdId, got.Value!) is not { } sealedList) return Reply(id, false, "That recovery code doesn't open your account's household.");
        var recovered = await _environment.Relay.RecoverAsync(_keys, session, Recovery.Verifier(codeKey), cancel).ConfigureAwait(false);
        if (!recovered.Ok || recovered.Value!.Household != householdId) return Reply(id, false, $"Couldn't recover your household: {recovered.Problem}.");
        var epoch = recovered.Value.Epoch;
        EnterLocked(householdId, got.Value!.Epoch, sealedList.Key, sealedList.Members, got.Value.Holder);
        _members.RemoveAllBut(_keys.DeviceId, epoch, _clock.GetUtcNow().ToUnixTimeMilliseconds());   // as the server removed them
        _store.RelayConfirmed = true;
        _relaySync.StartRotation(householdId, atLeast: epoch + 1);
        var next = RecoveryCode.New();
        _store.RecoveryKey = RecoveryCode.Key(RecoveryCode.Normalize(next)!);
        _store.AddPending(new PendingOp(PendingOp.RecoveryEnvelope, householdId, Replace: true));   // after the new key
        _recoveryMissing = false;
        ShowNewRecoveryCode(next);
        _log.LogInformation("Recovered the household with the recovery code; the others were removed at epoch {Epoch}", epoch);
        Publish();
        Kick();
        return Reply(id, true, "Your household is back on this PC.");
    }
}
