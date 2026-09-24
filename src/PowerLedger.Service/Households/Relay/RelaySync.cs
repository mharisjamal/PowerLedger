using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households.Relay;

/// <summary>Something the server still has to be told, kept until it has been: a household made, a member added or removed, a
/// new epoch's keys. Each names its household, so what leaving asks of the server still goes after this PC has left. A new
/// epoch's key waits in <see cref="HouseholdStore.RotationKey"/>, sealed only when it goes.</summary>
/// <param name="Replace">For the recovery: a new code, which replaces any other on the server (plan 0.9).</param>
internal sealed record PendingOp(
    string Kind, string Household, string? Device = null, string? Sign = null, string? Dh = null, int? Epoch = null, string? Proof = null,
    bool? Replace = null)
{
    public const string Create = "create";
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Keys = "keys";

    /// <summary>N2: the household's current key and member list in the account's recovery, sealed when it goes, with this PC's
    /// session: put only by the PC holding the code.</summary>
    public const string RecoveryEnvelope = "recovery";
}

/// <summary>What one relay run did.</summary>
internal sealed class RelayRun
{
    /// <summary>Things the user should hear of: a PC that left, this PC removed.</summary>
    public List<string> Notices { get; } = [];

    /// <summary>Why the run stopped short, in words the App can show; null when it didn't.</summary>
    public string? Problem { get; set; }

    /// <summary>True when this PC turned out to have been removed, and has now left.</summary>
    public bool Removed { get; set; }

    public int RowsIn { get; set; }

    public int RowsOut { get; set; }

    /// <summary>True once this run has read the member list.</summary>
    public bool MembersRead { get; set; }
}

/// <summary>Household keys sealed to members (households design §6): ECDH with each member's key-agreement key, HKDF, AES-GCM,
/// under a context naming the household and epoch, so an envelope can't be passed off as another's.</summary>
internal static class KeyWrap
{
    public static string Context(string householdId, int epoch) => $"household {householdId} epoch {epoch}";

    public static List<EnvelopeBody> For(DeviceKeys me, string householdId, int epoch, byte[] key, IEnumerable<HouseholdMember> members) =>
        [.. members.Select(member => new EnvelopeBody(
            member.DeviceId, Wire.Encode(HouseholdCrypto.WrapFor(me.Dh, member.DhKey, key, Context(householdId, epoch)))))];

    /// <summary>The key in an envelope sealed by the member with key-agreement key <paramref name="fromDh"/>; null when it doesn't open.</summary>
    public static byte[]? Open(DeviceKeys me, byte[] fromDh, string body, string householdId, int epoch)
    {
        if (Wire.Decode(body) is not { } wrapped) return null;
        try
        {
            var key = HouseholdCrypto.UnwrapFrom(me.Dh, fromDh, wrapped, Context(householdId, epoch));
            return key.Length == HouseholdCrypto.KeyLength ? key : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>How this PC stops being in a household: every member, this one too, is marked as left and their rows kept; the
/// household's keys and progress are forgotten (households design §6: a removed PC keeps what it had).</summary>
internal static class Membership
{
    public static void Forget(HouseholdStore store, HouseholdRepository household, long nowMs)
    {
        foreach (var member in household.Members()) household.MarkLeft(member.DeviceId, nowMs);
        store.LeaveHousehold();
    }
}

/// <summary>
/// Sync through the server (households design §5, plan 0.6), run every 15 minutes. First whatever the server still has to
/// be told, in order; then, every six hours, the member list, for who was removed; then this PC's rows that changed since
/// the last post, sealed with the household key as batches of at most 1 MB; all its rows again when a snapshot is due
/// (plan 0.9); then the other members' batches after the cursor, each opened under its epoch's key and the
/// associated data that ties it to its household, device, epoch and sequence number, once its sender's signature over it
/// checks against the sign key this PC holds for that member (plan 0.8); a batch under a newer epoch reads the member list
/// and this PC's envelope for it, whose sealer must be a member this PC knows. Each batch carries its sender's member list,
/// sealed, from which PCs added or removed elsewhere are learned; a batch's own device introduces nobody. The server's
/// member list only ever marks PCs as gone: members are never added on its word, so a key is never sealed to one the
/// server made up.
/// </summary>
internal sealed class RelaySync(HouseholdStore store, HouseholdRepository household, RelayClient relay, TimeProvider clock, ILogger log)
{
    private readonly MemberBook _members = new(store, household);

    public const int PageLimit = 100;
    public const int MaxPages = 20;
    public const int MaxPostBytes = 1_048_576;
    public const int MaxPlainBytes = 32 * 1024 * 1024;

    /// <summary>How often the member list is read when nothing suggests a change: a newer epoch in a batch reads it at once.</summary>
    public static readonly TimeSpan MembersEvery = TimeSpan.FromHours(6);

    /// <summary>How long a batch under a newer epoch waits for this PC's envelope before it is passed over.</summary>
    public static readonly TimeSpan KeyWait = TimeSpan.FromHours(24);

    /// <summary>How long a current member may keep posting under an older epoch before this PC makes a new key sealed to it too,
    /// as it may never have had an envelope for this PC's (plan 0.8).</summary>
    public static readonly TimeSpan LagWait = TimeSpan.FromHours(1);

    /// <summary>Runs the server may refuse a PC it hasn't taken as a member yet before the wait shows as a problem.</summary>
    public const int QuietRuns = 4;

    /// <summary>How far this PC's clock may be from the server's before its signed requests are refused.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);

    internal const string NotAddedYet = "The server hasn't taken this PC into the household yet. If this goes on, add it again from another PC in the household.";

    private int _unconfirmedRuns;
    private const int FirstChunk = 4000;

    /// <summary>One run. Stopping the service cancels it; a run stopped by <paramref name="cancel"/> leaves nothing half-done
    /// but the step under way, which the next run does again.</summary>
    public async Task<RelayRun> RunAsync(DeviceKeys keys, string name, ChassisKind kind, CancellationToken cancel)
    {
        var run = new RelayRun();
        try
        {
            await FlushAsync(keys, run, cancel).ConfigureAwait(false);
            if (store.HouseholdId is { } householdId && store.CurrentKey is not null)
            {
                if (store.MembersCheckedAt is not { } checkedAt || Now - checkedAt >= (long)MembersEvery.TotalMilliseconds)
                {
                    await RefreshMembersAsync(keys, householdId, run, cancel).ConfigureAwait(false);
                }
                if (!run.Removed)
                {
                    if (!RotationPending)                                       // plan 0.9: nothing goes under a key a new one waits to replace
                    {
                        await PostNewRowsAsync(keys, householdId, name, kind, run, cancel).ConfigureAwait(false);
                        await PostSnapshotAsync(keys, householdId, name, kind, run, cancel).ConfigureAwait(false);
                    }
                    await FetchAsync(keys, householdId, run, cancel).ConfigureAwait(false);
                }
            }
            store.Problem = run.Problem;
        }
        catch (RelayStop stop)
        {
            run.Problem = stop.Problem;
            store.Problem = stop.Problem;
        }
        return run;
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Takes the keys of the epochs after this PC's that the server holds for it, sealed by members it knows: as
    /// when an approval was refused for sealing an older key.</summary>
    public async Task CatchUpAsync(DeviceKeys keys, CancellationToken cancel)
    {
        if (store.HouseholdId is not { } householdId) return;
        for (var probe = 0; probe < 8; probe++)
        {
            var epoch = store.Epoch + 1;
            if (await FetchKeyAsync(keys, householdId, epoch, cancel).ConfigureAwait(false) is not { } key) return;
            store.AddKey(epoch, key);
        }
    }

    /// <summary>
    /// Tells the server what it still has to hear, oldest first. Each request belongs to a household (plan 0.9), and one that
    /// can't go yet holds up only the requests of its household after it. One the server won't ever take is dropped, and
    /// logged, but for a new key, which stays queued with nothing posted under the old key meanwhile. A 410 says this PC is
    /// no longer in that household: a removal or leave counts as done, and whatever else waits for it is dropped too. A
    /// household this PC is no longer in that doesn't take its requests at all (401) has nothing to be told.
    /// </summary>
    public async Task FlushAsync(DeviceKeys keys, RelayRun run, CancellationToken cancel)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        while (store.Pending.FirstOrDefault(waiting => !held.Contains(waiting.Household)) is { } op)
        {
            var mine = op.Household == store.HouseholdId;
            var result = op.Kind switch
            {
                PendingOp.Create => await relay.CreateHouseholdAsync(keys, op.Household, cancel).ConfigureAwait(false),
                PendingOp.Add when Wire.PublicKey(op.Sign) is { } sign && Wire.PublicKey(op.Dh) is { } dh && Wire.Decode(op.Proof) is { } proof =>
                    await relay.AddMemberAsync(keys, op.Household, sign, dh, proof, cancel).ConfigureAwait(false),
                PendingOp.Remove when op.Device is { } device =>
                    await relay.RemoveMemberAsync(keys, op.Household, device, cancel).ConfigureAwait(false),
                PendingOp.Keys when op.Household == store.HouseholdId => await PostRotationAsync(keys, op.Household, cancel).ConfigureAwait(false),
                PendingOp.Keys => new RelayResult<Done>(200, null, null),       // left since: nobody to rotate for
                PendingOp.RecoveryEnvelope => await PutRecoveryAsync(keys, op, cancel).ConfigureAwait(false),
                _ => new RelayResult<Done>(400, null, "it wasn't a request this PC can make"),
            };
            if (result.Ok)
            {
                Done(op);
                continue;
            }
            if (result.Status is 401 or 410)
            {
                if (mine) Refused(result);                                     // not added yet, or a clock far off
                else ClockRight();
            }
            if (result.Removed)
            {
                if (mine) WasRemoved(run);
                store.Pending = [.. store.Pending.Where(other => other.Household != op.Household)];
                continue;
            }
            if (!mine && result.Status == 401)
            {
                log.LogInformation("A household this PC is no longer in doesn't take its {Kind}; it is dropped", op.Kind);
            }
            else if (op.Kind == PendingOp.RecoveryEnvelope && result.Error == RecoveryWaits)
            {
                held.Add(op.Household);                                        // put once this PC has caught up with the key
                continue;
            }
            else if (result.Transient)
            {
                if (mine) throw new RelayStop($"Couldn't reach the server to update the household: {result.Problem}.");
                held.Add(op.Household);                                        // goes again later, holding up only its own household
                continue;
            }
            else if (op.Kind == PendingOp.Keys)
            {
                if (result.Error != WaitingForMembers)
                {
                    log.LogWarning("The server didn't take the household's new key ({Status}: {Problem}); it goes again later", result.Status, result.Problem);
                    run.Problem = $"The server didn't take the household's new key ({result.Problem}), so this PC sends nothing through it until it does.";
                }
                held.Add(op.Household);                                        // kept, and nothing posted under the old key meanwhile
                continue;
            }
            else if (!(op.Kind == PendingOp.Remove && result.Status == 404))  // already gone: removed by another, or it left
            {
                log.LogWarning("The server won't take the household's {Kind} ({Status}: {Problem}); it is dropped", op.Kind, result.Status, result.Problem);
                if (op.Kind == PendingOp.Add && result.Status == 409)
                {
                    run.Notices.Add("The household already has 16 PCs, so the newest one can only sync on the same network.");
                }
            }
            Done(op);
        }
    }

    /// <summary>
    /// Puts the account's recovery (plan 0.9): the current key and the member list, sealed under the code's key at the current
    /// epoch, with the verifier made from the code. Only the PC holding the code puts it, a new code replacing any other.
    /// When the server has a newer code, or none, the holder forgets its own; when the key moved on meanwhile, the put waits
    /// for this PC to catch up.
    /// </summary>
    private async Task<RelayResult<Done>> PutRecoveryAsync(DeviceKeys keys, PendingOp op, CancellationToken cancel)
    {
        if (store.Session is not { } session || store.RecoveryKey is not { } codeKey || op.Household != store.HouseholdId)
        {
            return new RelayResult<Done>(200, null, null);                     // signed out, forgotten or left since: nothing to put
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (store.CurrentKey is not { } key
                || Recovery.Envelope(codeKey, op.Household, store.Epoch, key, _members.Entries(compact: true), op.Replace == true) is not { } body)
            {
                log.LogWarning("The household's key and members don't fit in a recovery");
                return new RelayResult<Done>(200, null, null);
            }
            var put = await relay.PutRecoveryAsync(keys, session, body, cancel).ConfigureAwait(false);
            if (put.Ok || (put.Transient && put.Status != 401)) return put;
            if (put.Status == 401)
            {
                store.Session = null;                                          // the session has ended: signed out elsewhere
                return new RelayResult<Done>(200, null, null);
            }
            if (put.Status != 409) return new RelayResult<Done>(200, null, null);   // no longer a member: the household's routes say so
            var got = await relay.GetRecoveryAsync(keys, session, cancel).ConfigureAwait(false);
            if (op.Replace != true && (got.Status == 404 || (got.Value?.Holder is { } holder && holder != keys.DeviceId)))
            {
                log.LogInformation("The account's recovery has a newer code, or none; this PC forgets its own");
                store.RecoveryKey = null;
                store.RecoveryCodeToShow = null;
                return new RelayResult<Done>(200, null, null);
            }
            var before = store.Epoch;
            await CatchUpAsync(keys, cancel).ConfigureAwait(false);            // the key moved on: sealed again at the one the server is at
            if (store.Epoch == before) break;
        }
        return new RelayResult<Done>(409, null, RecoveryWaits);
    }

    /// <summary>Why the recovery waits: the household's key moved on while it was sealed.</summary>
    private const string RecoveryWaits = "the household's key moved on; the recovery goes once this PC has caught up";

    /// <summary>Takes a request the server has heard, or won't ever take, off the queue.</summary>
    private void Done(PendingOp op)
    {
        var pending = store.Pending.ToList();
        pending.Remove(op);
        store.Pending = pending;
    }

    /// <summary>True while a new key for this PC's household waits for the server.</summary>
    public bool RotationPending => store.HouseholdId is { } householdId && store.Pending.Any(op => op.Kind == PendingOp.Keys && op.Household == householdId);

    /// <summary>Finishes the new key waiting for the server, with what waits before it, as an adding PC does before it adds a
    /// PC back (plan 0.9).</summary>
    /// <returns>True once no new key waits.</returns>
    public async Task<bool> FinishRotationAsync(DeviceKeys keys, CancellationToken cancel)
    {
        if (!RotationPending) return true;
        try
        {
            await FlushAsync(keys, new RelayRun(), cancel).ConfigureAwait(false);
        }
        catch (RelayStop)
        {
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        return !RotationPending;
    }

    /// <summary>
    /// Starts a new key for the household (households design §6, plan 0.8), as when a PC is removed: made now and kept aside,
    /// sealed and posted when the server can be told, to the members it then lists as current, and used here only once it
    /// takes it. One at a time: a rotation already waiting covers every removal since, as it is sealed when it goes.
    /// </summary>
    /// <param name="atLeast">The epoch to make it for at the least, as when the server is known to be further on.</param>
    public void StartRotation(string householdId, int? atLeast = null)
    {
        if (store.HouseholdId != householdId || store.Pending.Any(op => op.Kind == PendingOp.Keys && op.Household == householdId)) return;
        var epoch = Math.Max(store.Epoch + 1, atLeast ?? 0);
        store.RotationKey = (epoch, HouseholdCrypto.NewKey());
        store.AddPending(new PendingOp(PendingOp.Keys, householdId, Epoch: epoch));
        log.LogInformation("A new key for the household waits to go to the server at epoch {Epoch}", epoch);
    }

    /// <summary>
    /// Posts the waiting new key (plan 0.9), sealed to exactly the members the server lists as current, which the server
    /// needs, each with the keys this PC holds for it. This PC never seals to a member it doesn't hold as current itself:
    /// while the server lists one it doesn't know, or knows as removed, the key waits, and nothing is posted under the old
    /// one, until the others' lists tell it of that member. The envelopes are kept until the key is taken, so a retry posts
    /// the very same bytes, which the server takes as done when its first answer was lost. When the members changed between
    /// the list and the post (400, or a 409 with the epoch still free), the list is read again. When the epoch is taken
    /// (409), this rotation is given up: the key there is taken if its sealer may hand it over, and a new rotation goes to
    /// the next epoch unless it was, and no PC removed here may hold it. Any other refusal leaves it queued. Once the server
    /// takes it, the key becomes this PC's current one, and the recovery envelope goes again with it.
    /// </summary>
    private async Task<RelayResult<Done>> PostRotationAsync(DeviceKeys keys, string householdId, CancellationToken cancel)
    {
        var rotation = store.RotationKey is { } pending && pending.Epoch > store.Epoch ? pending : NewRotation(store.Epoch + 1);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var listed = await relay.MembersAsync(keys, householdId, cancel).ConfigureAwait(false);
            if (!listed.Ok) return new RelayResult<Done>(listed.Status, null, listed.Error);
            var serverMembers = listed.Value!;
            foreach (var gone in serverMembers.Where(member => member.Removed is not null && member.Device != keys.DeviceId))
            {
                _members.RemovedByServer(gone.Device, gone.Removed!.Value, gone.RemovedEpoch, gone.AddedEpoch);
            }
            var staying = new List<HouseholdMember>();
            foreach (var listedMember in serverMembers.Where(member => member.Removed is null && member.Device != keys.DeviceId))
            {
                if (_members.Current(listedMember.Device) is not { } known || Wire.PublicKey(listedMember.Dh) is not { } dh || !dh.AsSpan().SequenceEqual(known.DhKey))
                {
                    log.LogInformation("The server lists {Device} as a member, which this PC doesn't know as one yet; the new key waits", listedMember.Device);
                    return new RelayResult<Done>(409, null, WaitingForMembers);
                }
                staying.Add(known);
            }
            staying.Add(new HouseholdMember(keys.DeviceId, "", default, keys.SignPublic, keys.DhPublic, 0, null, null));
            var result = await relay.PostKeysAsync(keys, householdId, rotation.Epoch, Envelopes(keys, householdId, rotation, staying), cancel).ConfigureAwait(false);
            if (result.Ok)
            {
                store.AddKey(rotation.Epoch, rotation.Key);
                store.RotationKey = null;
                if (store.Session is not null && store.RecoveryKey is not null) store.AddPending(new PendingOp(PendingOp.RecoveryEnvelope, householdId));
                log.LogInformation("The server took the household's new key; it is now at epoch {Epoch}", rotation.Epoch);
                return result;
            }
            if (result.Status == 400) continue;
            if (result.Status != 409) return result;
            var taken = rotation.Epoch;
            var there = await relay.GetKeyAsync(keys, householdId, taken, cancel).ConfigureAwait(false);
            if (there.Status == 404) continue;                                 // the epoch is still free: the members changed meanwhile
            if (!there.Ok)
            {
                if (there.Transient) throw new RelayStop($"Couldn't reach the server to update the household: {there.Problem}.");
                return new RelayResult<Done>(there.Status, null, there.Error);
            }
            store.RotationKey = null;                                          // taken: given up
            if (Opened(keys, householdId, taken, there.Value!) is { } theirs) store.AddKey(taken, theirs);
            if (store.Epoch >= taken && !MayHold(serverMembers, taken))
            {
                log.LogInformation("Another member's new key came first, at epoch {Epoch}; this PC took it", taken);
                return new RelayResult<Done>(200, null, null);
            }
            rotation = NewRotation(Math.Max(store.Epoch, taken) + 1);
            log.LogInformation("The household's epoch {Epoch} was taken by a key this PC can't use; rotating on to {Next}", taken, rotation.Epoch);
        }
        return new RelayResult<Done>(503, null, "the household's key kept moving on; it goes again later");
    }

    /// <summary>Why a new key waits: the server lists a member this PC doesn't hold as current yet.</summary>
    private const string WaitingForMembers = "the server lists a member this PC doesn't know yet";

    /// <summary>A new key for <paramref name="epoch"/>, kept aside until the server takes it.</summary>
    private (int Epoch, byte[] Key) NewRotation(int epoch)
    {
        (int, byte[]) rotation = (epoch, HouseholdCrypto.NewKey());
        store.RotationKey = rotation;
        return rotation;
    }

    /// <summary>The rotation's key sealed to each of <paramref name="staying"/>: the envelope already sealed for a member, as
    /// last posted, and a new one for a member without, all kept for the next try.</summary>
    private List<EnvelopeBody> Envelopes(DeviceKeys keys, string householdId, (int Epoch, byte[] Key) rotation, List<HouseholdMember> staying)
    {
        var kept = store.RotationPost is { } post && post.Epoch == rotation.Epoch
            ? post.Envelopes.ToDictionary(envelope => envelope.Device, StringComparer.Ordinal)
            : [];
        List<EnvelopeBody> envelopes =
            [.. staying.Select(member => kept.GetValueOrDefault(member.DeviceId) ?? KeyWrap.For(keys, householdId, rotation.Epoch, rotation.Key, [member])[0])];
        store.RotationPost = new PostKeysBody(rotation.Epoch, envelopes);
        return envelopes;
    }

    /// <summary>True when a PC removed here may hold the key of <paramref name="epoch"/>: the server lists it as current, or as
    /// removed only at that epoch or later, so it was a member when that key went out.</summary>
    private bool MayHold(IEnumerable<ServerMember> serverMembers, int epoch) => serverMembers.Any(member =>
        _members.EpochsOf(member.Device) is { Current: false } && (member.Removed is null || member.RemovedEpoch is not { } at || at >= epoch));

    /// <summary>Reads the member list for who has gone: members the server says were removed are marked as removed (plan 0.9:
    /// its list only ever removes), and this PC itself, when it was removed, leaves. A PC gone means a new key (households design §6, plan 0.8): the next epochs'
    /// envelopes are looked for, as nothing else says there is one until a batch under it comes, and this PC, staying, makes
    /// one of its own, since the PC that went may have left without one.</summary>
    private async Task RefreshMembersAsync(DeviceKeys keys, string householdId, RelayRun run, CancellationToken cancel)
    {
        run.MembersRead = true;
        var result = await relay.MembersAsync(keys, householdId, cancel).ConfigureAwait(false);
        if (!Check(result, run)) return;
        store.RelayConfirmed = true;
        store.MembersCheckedAt = Now;
        var gone = 0;
        foreach (var member in result.Value!)
        {
            if (member.Removed is not { } removed) continue;
            if (member.Device == keys.DeviceId)
            {
                WasRemoved(run);
                return;
            }
            var name = household.Member(member.Device)?.Name;
            if (_members.RemovedByServer(member.Device, removed, member.RemovedEpoch, member.AddedEpoch))
            {
                run.Notices.Add($"{name} is no longer in the household.");
                gone++;
            }
        }
        for (var probe = 0; gone > 0 && probe < 3; probe++)
        {
            var epoch = store.Epoch + 1;
            if (await FetchKeyAsync(keys, householdId, epoch, cancel).ConfigureAwait(false) is not { } key) break;
            store.AddKey(epoch, key);
        }
        if (gone > 0) StartRotation(householdId);
    }

    private void WasRemoved(RelayRun run)
    {
        log.LogInformation("This PC was removed from its household");
        Membership.Forget(store, household, Now);
        run.Removed = true;
        run.Notices.Add("This PC was removed from the household.");
    }

    /// <summary>A refusal from a household route: 410 says this PC was removed; anything else is a problem to show. Before
    /// the server has taken a request from this PC as a member of this household, a 401 or a 410 means only that it hasn't
    /// been added, or added again, yet (<see cref="Refused"/>).</summary>
    /// <returns>True for an answer to go on with.</returns>
    private bool Check<T>(RelayResult<T> result, RelayRun run)
    {
        if (result.Ok)
        {
            _unconfirmedRuns = 0;
            return true;
        }
        if (result.Status is 401 or 410) Refused(result);
        if (result.Removed)
        {
            WasRemoved(run);
            return false;
        }
        if (result.Transient) throw new RelayStop($"Couldn't sync through the server: {result.Problem}.");
        log.LogWarning("The server refused a household request ({Status}: {Problem})", result.Status, result.Problem);
        throw new RelayStop($"The server refused to sync: {result.Problem}.");
    }

    /// <summary>
    /// A 401 or a 410 for this PC's household. With this PC's clock far from the server's, as its answers' Date shows, the
    /// server refuses every signed request: said at once. Before the server has taken a request from this PC as a member of
    /// this household, as just after joining or being added again after a removal it hadn't heard of, the PC that added it
    /// may not have told the server yet: the run stops quietly, and says so only after <see cref="QuietRuns"/> runs.
    /// Otherwise it returns, and the refusal is taken as it is.
    /// </summary>
    private void Refused<T>(RelayResult<T> result)
    {
        ClockRight();
        if (store.RelayConfirmed) return;
        throw new RelayStop(++_unconfirmedRuns >= QuietRuns ? NotAddedYet : null);
    }

    /// <summary>Stops the run when this PC's clock is so far from the server's, as its answers' Date shows, that the server
    /// refuses every signed request.</summary>
    private void ClockRight()
    {
        if (relay.Skew is { } skew && skew.Duration() > ClockSlack)
        {
            var minutes = (int)Math.Round(skew.Duration().TotalMinutes);
            throw new RelayStop($"This PC's clock is {minutes} minutes {(skew > TimeSpan.Zero ? "ahead" : "behind")}, so the server refuses its requests. Set the clock right.");
        }
    }

    /// <summary>This PC's rows that changed since the last post, oldest change first; a post the server stops part way
    /// through, as at its daily limit, goes on from the last batch it took.</summary>
    private async Task PostNewRowsAsync(DeviceKeys keys, string householdId, string name, ChassisKind kind, RelayRun run, CancellationToken cancel)
    {
        var rows = store.PostedHour is { } hour
            ? household.ChangedAfter(keys.DeviceId, store.PostedThrough, hour)
            : household.ChangedAfter(keys.DeviceId, store.PostedThrough);
        if (rows.Count == 0) return;
        await PostRowsAsync(keys, householdId, name, kind, rows, run, last =>
        {
            store.PostedThrough = last.ChangedMs;
            store.PostedHour = last.HourMs;
        }, cancel).ConfigureAwait(false);
        store.PostedHour = null;                                               // every row changed then has gone
    }

    /// <summary>How often this PC posts all its rows again at the least, and at the most, unless the epoch changed (plan 0.9).</summary>
    public static readonly TimeSpan SnapshotAtLeast = TimeSpan.FromDays(30);

    public static readonly TimeSpan SnapshotAtMost = TimeSpan.FromDays(1);

    /// <summary>
    /// Posts all this PC's rows of the last 13 months again (plan 0.9), so a PC that was away, or is new, reads the whole
    /// history with the current key, whatever expired on the server: under a new key once it takes effect, as on entering
    /// a household; a day or more after the last time once a member has joined since; and every 30 days. One cut short, as
    /// by the server's daily limit, goes on from its last batch, unless the key has changed since.
    /// </summary>
    private async Task PostSnapshotAsync(DeviceKeys keys, string householdId, string name, ChassisKind kind, RelayRun run, CancellationToken cancel)
    {
        var epoch = store.Epoch;
        var since = Now - (store.SnapshotAt ?? long.MinValue / 2);
        var resuming = store.SnapshotFrom is { } from && from.Epoch == epoch ? from.Hour : (long?)null;
        if (resuming is null && store.SnapshotEpoch == epoch && since < (long)SnapshotAtLeast.TotalMilliseconds
            && !(store.SnapshotWanted && since >= (long)SnapshotAtMost.TotalMilliseconds))
        {
            return;
        }
        var now = clock.GetUtcNow();
        var start = Math.Max(HourRows.BackfillFrom(now).ToUnixTimeMilliseconds(), (resuming ?? -1) + 1);
        var rows = household.RowsBetween(keys.DeviceId, start, now.ToUnixTimeMilliseconds());
        if (rows.Count > 0) await PostRowsAsync(keys, householdId, name, kind, rows, run, last => store.SnapshotFrom = (epoch, last.HourMs), cancel).ConfigureAwait(false);
        store.SnapshotFrom = null;
        store.SnapshotEpoch = epoch;
        store.SnapshotAt = Now;
        store.SnapshotWanted = false;
        log.LogInformation("Posted all this PC's rows again, under epoch {Epoch}", epoch);
    }

    /// <summary>Posts the rows as batches under the current key, each at most 1 MB as posted: a batch that would be larger
    /// is split in two until it fits. <paramref name="posted"/> hears of the last row of each batch the server took.</summary>
    private async Task PostRowsAsync(
        DeviceKeys keys, string householdId, string name, ChassisKind kind, List<HouseholdRow> rows, RelayRun run, Action<HouseholdRow> posted,
        CancellationToken cancel)
    {
        var epoch = store.Epoch;
        var key = store.CurrentKey ?? throw new RelayStop(null);
        var device = new WireMember(keys.DeviceId, name, Wire.Kind(kind));
        var members = _members.Entries();
        var start = 0;
        var size = FirstChunk;
        while (start < rows.Count)
        {
            var take = Math.Min(size, rows.Count - start);
            var plain = HouseholdJson.Bytes(new BatchPlain(1, device, [.. rows.Skip(start).Take(take).Select(Wire.Row)], members), HouseholdJson.Default.BatchPlain);
            var packed = SharingClient.Gzip(plain);
            if (PostedSize(packed.Length) > MaxPostBytes && take > 1)
            {
                size = take / 2;
                continue;
            }
            var seq = store.NextSequence();
            var aad = HouseholdCrypto.BatchAad(householdId, keys.DeviceId, epoch, seq);
            var sealedBody = HouseholdCrypto.Seal(key, packed, aad);
            var sig = HouseholdCrypto.SignData(keys.Sign, HouseholdCrypto.BatchToSign(aad, sealedBody));
            var result = await relay.PostBatchAsync(keys, householdId, new BatchPost(keys.DeviceId, epoch, seq, Wire.Encode(sealedBody), Wire.Encode(sig)), cancel)
                .ConfigureAwait(false);
            if (!Check(result, run)) throw new RelayStop(null);
            store.RelayConfirmed = true;
            run.RowsOut += take;
            start += take;
            posted(rows[start - 1]);
        }
    }

    /// <summary>The size of a batch as posted: the sealed body and its signature in base64url, and the JSON around them.</summary>
    private static long PostedSize(int packed) => (packed + 28 + 2) / 3 * 4 + 384;

    /// <summary>
    /// Reads the other members' batches after the cursor, a page at a time. A batch that can't be opened yet, under a newer
    /// epoch whose key isn't here or from a PC not yet introduced, holds the cursor where it is: the pages after it are still
    /// read, for the members they introduce, and the held batches tried again at the end; the cursor moves past them only
    /// once none is left waiting. What was kept from pages read again is kept again. After <see cref="KeyWait"/> a batch
    /// still waiting is passed over.
    /// </summary>
    private async Task FetchAsync(DeviceKeys keys, string householdId, RelayRun run, CancellationToken cancel)
    {
        var after = store.RelayCursor;
        var giveUp = store.WaitingSince is { } since && Now - since >= (long)KeyWait.TotalMilliseconds;
        var held = new List<BatchItem>();
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await relay.BatchesAsync(keys, householdId, after, PageLimit, cancel).ConfigureAwait(false);
            if (!Check(result, run)) return;
            store.RelayConfirmed = true;
            foreach (var item in result.Value!.Items ?? [])
            {
                var opened = await OpenAsync(keys, householdId, item, run, cancel).ConfigureAwait(false);
                if (run.Removed) return;
                if (opened is null) held.Add(item);
                run.RowsIn += opened ?? 0;
            }
            after = Math.Max(after, result.Value.Next);
            if (held.Count == 0) store.RelayCursor = after;
            if (!result.Value.More) break;
        }
        RotateForLagging(householdId);
        if (held.Count == 0)
        {
            store.WaitingSince = null;
            return;
        }
        var stillHeld = 0;
        foreach (var item in held)
        {
            var opened = await OpenAsync(keys, householdId, item, run, cancel).ConfigureAwait(false);
            if (run.Removed) return;
            if (opened is null) stillHeld++;
            run.RowsIn += opened ?? 0;
        }
        if (stillHeld == 0 || giveUp)
        {
            store.RelayCursor = after;
            store.WaitingSince = null;
        }
        else
        {
            store.WaitingSince ??= Now;
        }
    }

    /// <summary>
    /// Opens one batch and keeps its rows (plan 0.8, 0.9). The batch must be signed by the member it says it is from, with the
    /// sign key this PC holds for it: a PC this one doesn't know waits to be introduced, and a batch that isn't signed, or
    /// doesn't open, is passed over. A newer epoch than this PC's sends it to the member list first, for who was removed,
    /// then for its envelope. From a current member, the sealed member list teaches the members added and removed
    /// elsewhere, and the sender's own entry its name and kind; from a removed one nothing is learned, and only rows under
    /// an epoch no later than its removal are kept. A row's change time is taken as no later than a day from now.
    /// </summary>
    /// <returns>How many rows were newer than those kept; null when the batch can't be opened yet: its epoch's key isn't
    /// here, or its PC hasn't been introduced.</returns>
    private async Task<int?> OpenAsync(DeviceKeys keys, string householdId, BatchItem item, RelayRun run, CancellationToken cancel)
    {
        if (!Wire.IsDeviceId(item.Device) || item.Device == keys.DeviceId || item.Epoch <= 0) return 0;
        var member = household.Member(item.Device);
        if (_members.EpochsOf(item.Device) is { Current: false } && (member is null || !_members.MayHavePosted(item.Device, item.Epoch)))
        {
            return 0;                                                          // removed: nothing of it after its removal, nor once its rows went
        }
        if (member is null) return null;                                       // not introduced yet by a member's list
        if (Wire.Decode(item.Body) is not { } sealedBody || Wire.Decode(item.Sig) is not { } sig) return 0;
        var aad = HouseholdCrypto.BatchAad(householdId, item.Device, item.Epoch, item.Seq);
        var signed = HouseholdCrypto.BatchToSign(aad, sealedBody);
        if (!HouseholdCrypto.Verify(member.SignKey, signed, sig))
        {
            log.LogWarning("A batch said to be from {Device} isn't signed by it, so it was passed over", item.Device);
            return 0;
        }
        var key = store.KeyFor(item.Epoch);
        if (key is null && item.Epoch > store.Epoch)
        {
            if (!run.MembersRead)
            {
                await RefreshMembersAsync(keys, householdId, run, cancel).ConfigureAwait(false);
                if (run.Removed) return 0;
            }
            key = await FetchKeyAsync(keys, householdId, item.Epoch, cancel).ConfigureAwait(false);
            if (key is null) return null;
            store.AddKey(item.Epoch, key);
        }
        if (key is null) return 0;
        BatchPlain? batch;
        try
        {
            var packed = HouseholdCrypto.Open(key, sealedBody, aad);
            batch = JsonSerializer.Deserialize(Gunzip(packed), HouseholdJson.Default.BatchPlain);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or JsonException or IOException)
        {
            log.LogWarning("A batch from {Device} didn't open, so it was passed over", item.Device);
            return 0;
        }
        if (batch?.Device is not { } sender || sender.Id != item.Device) return 0;
        if (_members.Current(item.Device) is not null)
        {
            if (Wire.Name(sender.Name) is { } name && Wire.Kind(sender.Kind) is { } kind && (name != member.Name || kind != member.Kind))
            {
                household.SaveMember(member with { Name = name, Kind = kind });
            }
            var learned = _members.Learn(batch.Members ?? [], item.Device, keys.DeviceId, Now);   // never from a removed PC (plan 0.9)
            foreach (var gone in learned.Removed)
            {
                if (household.Member(gone) is { } left) run.Notices.Add($"{left.Name} is no longer in the household.");
            }
            if (learned.Removed.Count > 0) StartRotation(householdId);
            NoteEpoch(item.Device, item.Epoch);
        }
        var rows = Wire.CapChanged((batch.Rows ?? []).Select(row => Wire.Row(item.Device, row)).OfType<HouseholdRow>(), Now);
        var taken = household.Upsert(rows);
        if (rows.Count > 0) household.Synced(item.Device, Math.Min(Now, rows.Max(row => row.ChangedMs)));
        return taken;
    }


    /// <summary>Notes a current member posting under an older epoch than this PC's, or forgets that it did once it catches up.</summary>
    private void NoteEpoch(string device, int epoch)
    {
        var lagging = store.Lagging;
        if (epoch >= store.Epoch)
        {
            if (!lagging.ContainsKey(device)) return;
            var caughtUp = new Dictionary<string, Lag>(lagging, StringComparer.Ordinal);
            caughtUp.Remove(device);
            store.Lagging = caughtUp;
        }
        else if (!lagging.ContainsKey(device))
        {
            store.Lagging = new Dictionary<string, Lag>(lagging, StringComparer.Ordinal) { [device] = new Lag(Now) };
        }
    }

    /// <summary>A current member still behind after <see cref="LagWait"/> may have no envelope for this PC's epoch, as when it
    /// was approved while another member made a new key without it: this PC makes a new key, which is sealed to every current
    /// member, once for each epoch it is at.</summary>
    private void RotateForLagging(string householdId)
    {
        var lagging = store.Lagging;
        var due = lagging.Where(pair => Now - pair.Value.Since >= (long)LagWait.TotalMilliseconds && pair.Value.RotatedAt != store.Epoch
            && _members.Current(pair.Key) is not null).Select(pair => pair.Key).ToList();
        if (due.Count == 0) return;
        log.LogInformation("{Count} members still post under an older key; making a new one sealed to them too", due.Count);
        StartRotation(householdId);
        var marked = new Dictionary<string, Lag>(lagging, StringComparer.Ordinal);
        var epoch = store.RotationKey?.Epoch ?? store.Epoch + 1;
        foreach (var device in due) marked[device] = new Lag(Now, epoch);          // time to catch up with the new key
        store.Lagging = marked;
    }

    /// <summary>This PC's envelope for an epoch, opened with the key of the member that sealed it; null when there is none, or
    /// when its sealer may not hand over that epoch's key (plan 0.9): it must be current here and added before the epoch,
    /// since a removed PC holds every key it sealed.</summary>
    private async Task<byte[]?> FetchKeyAsync(DeviceKeys keys, string householdId, int epoch, CancellationToken cancel)
    {
        var result = await relay.GetKeyAsync(keys, householdId, epoch, cancel).ConfigureAwait(false);
        if (!result.Ok)
        {
            if (result.Transient) throw new RelayStop($"Couldn't sync through the server: {result.Problem}.");
            return null;
        }
        return Opened(keys, householdId, epoch, result.Value!);
    }

    /// <summary>The key in this PC's envelope for an epoch, when its sealer may hand it over (plan 0.9); null otherwise.</summary>
    private byte[]? Opened(DeviceKeys keys, string householdId, int epoch, KeyEnvelopeReply reply)
    {
        if (reply.Epoch != epoch || !_members.MaySeal(reply.From, epoch) || household.Member(reply.From) is not { } sealer)
        {
            log.LogWarning("The key for epoch {Epoch} came from {Sealer}, which may not hand it over, so it wasn't taken", epoch, reply.From);
            return null;
        }
        return KeyWrap.Open(keys, sealer.DhKey, reply.Body, householdId, epoch);
    }

    /// <summary>Gunzips at most <see cref="MaxPlainBytes"/>, so a batch can't fill the service's memory.</summary>
    internal static byte[] Gunzip(byte[] packed)
    {
        using var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (plain.Length + read > MaxPlainBytes) throw new InvalidDataException("A batch unpacks to more than 32 MB.");
            plain.Write(buffer, 0, read);
        }
        return plain.ToArray();
    }

    /// <summary>Ends the run: with words for the status, or quietly when there is nothing to tell.</summary>
    private sealed class RelayStop(string? problem) : Exception(problem ?? "The run stopped.")
    {
        public string? Problem { get; } = problem;
    }
}
