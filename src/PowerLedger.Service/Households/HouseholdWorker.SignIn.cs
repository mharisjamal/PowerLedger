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

/// <summary>N2: the recovery this PC opened, with the key and member list sealed at <paramref name="Epoch"/>, and the verifier
/// it shows the server, kept while it takes the household back (plan 0.10).</summary>
internal sealed record Recovering(string Household, int Epoch, string Key, List<WireMember> Members, string Verifier);

/// <summary>N2: a new recovery code for <paramref name="Household"/>, kept until the server has it (plan 0.10), and the body
/// last put with it; null before the first put.</summary>
internal sealed record RecoveryPut(string Household, string Code, RecoveryBody? Body = null);

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

        if (PairingCommitted) return Reply(request.Id, false, Busy);           // plan 0.10: it waits for the pairing to finish
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
            if (_store.Recovering is not null) Kick();                         // the recover goes again at once
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
            return await MakeRecoveryAsync(session, householdId, deadline.Token).ConfigureAwait(false) switch
            {
                true => Reply(request.Id, true, "A new recovery code was made."),
                null => Reply(request.Id, false, RecoveryCodeWaits),
                false => Reply(request.Id, false, "Couldn't make a new recovery code. Check this PC is online and try again."),
            };
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Reply(request.Id, false, "The server didn't answer in time. Try again.");
        }
    }

    /// <summary>Said when a new recovery code's put got no answer: it goes again, and the code shows once the server has it.</summary>
    internal const string RecoveryCodeWaits = "The server didn't answer. This PC tries again, and shows the new recovery code once the server has it.";

    /// <summary>Makes a new recovery code for the household (plan 0.10), kept before it is put (<see cref="PutRecoveryCodeAsync"/>).</summary>
    /// <returns>True once the server has it; null while its put waits to go again; false when it won't go.</returns>
    private Task<bool?> MakeRecoveryAsync(string session, string householdId, CancellationToken cancel)
    {
        _store.RecoveryPut = new RecoveryPut(householdId, RecoveryCode.New());
        return PutRecoveryCodeAsync(session, cancel);
    }

    /// <summary>
    /// Puts the new recovery code kept (plan 0.10): the key and member list sealed under the code's key at the current epoch,
    /// in place of any other code, which makes this PC the one holding it. A put whose answer was lost goes again as it was,
    /// at later turns; one refused because the key moved on is sealed again at the key the server is at; and while a new key
    /// waits for the server, as after a recovery, it waits for that key. Once the server has it, this PC keeps the code's
    /// key, to put the recovery again with each new key, and shows the code once as a <see cref="NoticeKind.RecoveryCode"/>
    /// notice, kept encrypted until the App says it was seen.
    /// </summary>
    /// <returns>True once the server has it; null while it waits to go again; false when it won't go.</returns>
    private async Task<bool?> PutRecoveryCodeAsync(string session, CancellationToken cancel)
    {
        if (_store.RecoveryPut is not { } kept) return false;
        if (kept.Household != _store.HouseholdId)
        {
            _store.RecoveryPut = null;                                         // left since: nothing to put
            return false;
        }
        if (_relaySync.RotationPending) return null;                           // sealed at the new key, once the server has it
        var codeKey = RecoveryCode.Key(RecoveryCode.Normalize(kept.Code)!);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = kept.Body is { } last && last.Epoch == _store.Epoch ? last
                : _store.CurrentKey is { } key ? Recovery.Envelope(codeKey, kept.Household, _store.Epoch, key, _members.Entries(compact: true), replace: true)
                : null;
            if (body is null)
            {
                _log.LogWarning("The household's key and members don't fit in a recovery");
                _store.RecoveryPut = null;
                return false;
            }
            if (kept.Body != body)
            {
                kept = kept with { Body = body };
                _store.RecoveryPut = kept;                                     // kept before it goes: a lost answer, the same put again
            }
            var put = await _environment.Relay.PutRecoveryAsync(_keys, session, body, cancel).ConfigureAwait(false);
            if (put.Ok)
            {
                _store.RecoveryKey = codeKey;
                _store.RecoveryPut = null;
                _recoveryMissing = false;
                ShowNewRecoveryCode(kept.Code);
                return true;
            }
            _log.LogWarning("The recovery couldn't be kept with the account ({Problem})", put.Problem);
            if (put.Status == 401) _store.Session = null;                      // the session has ended: kept for the next sign-in
            if (put.Transient) return null;
            if (put.Status != 409)
            {
                _store.RecoveryPut = null;
                return false;
            }
            await _relaySync.CatchUpAsync(_keys, cancel).ConfigureAwait(false);   // the key moved on: sealed again at the one the server is at
        }
        return null;
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

    /// <summary>Forgets this PC's recovery code and its key, and any recovery or new code under way, as on signing out or
    /// signing in as another account.</summary>
    private void ForgetRecovery()
    {
        _store.RecoveryKey = null;
        _store.RecoveryCodeToShow = null;
        _store.Recovering = null;
        _store.RecoveryPut = null;
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
        var waiting = _store.RecoveryPut?.Household == householdId
            || _store.Pending.Any(op => op.Kind == PendingOp.RecoveryEnvelope && op.Household == householdId && op.Replace == true);
        _recoveryMissing = holder is null || (holder != _keys.DeviceId && _members.Current(holder) is null)
            || (holder == _keys.DeviceId && _store.RecoveryKey is null && !waiting);   // plan 0.10: the holder, but without the code's key
        if (_store.RecoveryKey is not null && holder != _keys.DeviceId && !waiting)
        {
            _log.LogInformation("The account's recovery has a newer code, or none; this PC forgets its own");
            _store.RecoveryKey = null;
            _store.RecoveryCodeToShow = null;
        }
    }

    /// <summary>
    /// Takes the account's household back with the recovery code (households design §7, plan 0.10): opens the recovery with
    /// the code's key, at whatever epoch it was sealed, keeps it, and proves the code to the server, which makes this PC the
    /// household's only current member, removing every other at the household's current epoch. A recover whose answer was
    /// lost goes again, at once and at later turns, from what was kept (<see cref="RecoverKeptAsync"/>).
    /// </summary>
    private async Task<PipeMessage> RecoverLockedAsync(long id, string session, string householdId, string code, CancellationToken cancel)
    {
        var verifier = Wire.Encode(Recovery.Verifier(RecoveryCode.Key(code)));
        if (_store.Recovering is not { } recovering || recovering.Household != householdId || recovering.Verifier != verifier)
        {
            var got = await _environment.Relay.GetRecoveryAsync(_keys, session, cancel).ConfigureAwait(false);
            if (got.Status == 404) return Reply(id, false, "Your account has no household to recover.");
            if (!got.Ok) return Reply(id, false, $"Couldn't recover your household: {got.Problem}.");
            if (Recovery.Open(RecoveryCode.Key(code), householdId, got.Value!) is not { } sealedList)
            {
                return Reply(id, false, "That recovery code doesn't open your account's household.");
            }
            recovering = new Recovering(householdId, got.Value!.Epoch, Wire.Encode(sealedList.Key), sealedList.Members, verifier);
            _store.Recovering = recovering;                                    // kept before the server is asked (plan 0.10)
        }
        for (var attempt = 0; ; attempt++)
        {
            var (entered, problem) = await RecoverKeptAsync(recovering, session, cancel).ConfigureAwait(false);
            if (entered == true) return Reply(id, true, "Your household is back on this PC.");
            if (entered == false) return Reply(id, false, $"Couldn't recover your household: {problem}.");
            if (_store.Session is null) return Reply(id, false, "Your session has ended. Sign in again with the recovery code.");
            if (attempt == 2)
            {
                Kick();
                return Reply(id, false, $"The server didn't answer ({problem}). This PC tries again.");
            }
        }
    }

    /// <summary>A recovery under way whose answer was lost goes again with each turn (plan 0.10), and says how it ended.</summary>
    private async Task ResumeRecoveryAsync(CancellationToken cancel)
    {
        if (_store.Recovering is not { } recovering || _store.Session is not { } session) return;
        if (_store.HouseholdId is { } mine && mine != recovering.Household)
        {
            _store.Recovering = null;                                          // in another household since: never recovered over it
            return;
        }
        var (entered, problem) = await RecoverKeptAsync(recovering, session, cancel).ConfigureAwait(false);
        if (entered == true) Info("Your household is back on this PC.");
        else if (entered == false) Info($"Your household couldn't be recovered: {problem}.");
    }

    /// <summary>
    /// Asks the server to make this PC the household's only current member with the recovery kept (plan 0.10). The server
    /// answers a retry by the same PC within 10 minutes as it answered the recover; after that a 404 may still mean a
    /// recover of this PC's went through unheard, which the member list, readable only by a member, shows: this PC is its
    /// only current member. Then the code is used up: this PC doesn't keep it, makes a new key without the others, and a new
    /// code, which goes to the server once the key has.
    /// </summary>
    /// <returns>True once this PC is in; null while the server's answer is still to come; false when it won't be.</returns>
    private async Task<(bool? Entered, string? Problem)> RecoverKeptAsync(Recovering recovering, string session, CancellationToken cancel)
    {
        var recovered = await _environment.Relay.RecoverAsync(_keys, session, Wire.Decode(recovering.Verifier)!, cancel).ConfigureAwait(false);
        int epoch;
        if (recovered.Ok && recovered.Value!.Household == recovering.Household)
        {
            epoch = recovered.Value.Epoch;
        }
        else if (recovered.Status == 401)
        {
            _store.Session = null;                                             // the session has ended: signing in again goes on with it
            return (null, recovered.Problem);
        }
        else if (recovered.Transient)
        {
            return (null, recovered.Problem);                                  // its answer maybe lost: kept, and asked again
        }
        else if (recovered.Status == 404 && await RecoveredUnheardAsync(recovering, cancel).ConfigureAwait(false) is { } at)
        {
            epoch = at;
        }
        else
        {
            _store.Recovering = null;
            return (false, recovered.Ok ? "the server named another household" : recovered.Problem);
        }
        var householdId = recovering.Household;
        CancelPairingUnderWay();                                               // plan 0.9: joining by sign-in stops a pairing under way
        EnterLocked(householdId, recovering.Epoch, Wire.Decode(recovering.Key)!, recovering.Members, null, null);   // the code vouches for the whole list
        _store.Recovering = null;
        _members.RemoveAllBut(_keys.DeviceId, epoch, _clock.GetUtcNow().ToUnixTimeMilliseconds());   // as the server removed them
        _store.RelayConfirmed = true;
        _relaySync.StartRotation(householdId, atLeast: epoch + 1, forRemoval: true);
        _store.RecoveryKey = null;
        _store.RecoveryPut = new RecoveryPut(householdId, RecoveryCode.New());   // put after the new key, and shown then
        _recoveryMissing = false;
        _log.LogInformation("Recovered the household with the recovery code; the others were removed at epoch {Epoch}", epoch);
        Publish();
        Kick();
        return (true, null);
    }

    /// <summary>The epoch a recover of this PC's took the household at, when the member list shows it went through though its
    /// answer never came: this PC, outside the household until now, is its only current member. Null otherwise.</summary>
    private async Task<int?> RecoveredUnheardAsync(Recovering recovering, CancellationToken cancel)
    {
        if (_store.HouseholdId == recovering.Household) return null;
        var listed = await _environment.Relay.MembersAsync(_keys, recovering.Household, cancel).ConfigureAwait(false);
        if (!listed.Ok || listed.Value!.Where(member => member.Removed is null).Select(member => member.Device).ToList() is not [var only] || only != _keys.DeviceId)
        {
            return null;
        }
        return listed.Value!.Max(member => Math.Max(member.AddedEpoch, member.RemovedEpoch ?? 0));
    }
}
