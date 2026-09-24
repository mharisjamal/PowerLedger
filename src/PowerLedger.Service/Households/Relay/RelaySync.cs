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
/// new epoch's keys. Each names its household, so what leaving asks of the server still goes after this PC has left.</summary>
internal sealed record PendingOp(
    string Kind, string Household, string? Device = null, string? Sign = null, string? Dh = null, int? Epoch = null,
    List<EnvelopeBody>? Envelopes = null)
{
    public const string Create = "create";
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Keys = "keys";
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
/// the last post, sealed with the household key as batches of at most 1 MB; its year of rows once for each member it
/// hasn't posted it for; then the other members' batches after the cursor, each opened under its epoch's key and the
/// associated data that ties it to its household, device, epoch and sequence number; a batch under a newer epoch reads the
/// member list and this PC's envelope for it, whose sealer must be a member this PC knows. A batch teaches its sender's name,
/// kind and keys, but a PC not yet known is taken only from a batch sealed with the current key, which a removed PC
/// doesn't have. The server's member list only ever marks PCs as gone: members are never added on its word, so a key is
/// never sealed to one the server made up.
/// </summary>
internal sealed class RelaySync(HouseholdStore store, HouseholdRepository household, RelayClient relay, TimeProvider clock, ILogger log)
{
    public const int PageLimit = 100;
    public const int MaxPages = 20;
    public const int MaxPostBytes = 1_048_576;
    public const int MaxPlainBytes = 32 * 1024 * 1024;

    /// <summary>How often the member list is read when nothing suggests a change: a newer epoch in a batch reads it at once.</summary>
    public static readonly TimeSpan MembersEvery = TimeSpan.FromHours(6);

    /// <summary>How long a batch under a newer epoch waits for this PC's envelope before it is passed over.</summary>
    public static readonly TimeSpan KeyWait = TimeSpan.FromHours(24);
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
                    await PostNewRowsAsync(keys, householdId, name, kind, run, cancel).ConfigureAwait(false);
                    await PostHistoryAsync(keys, householdId, name, kind, run, cancel).ConfigureAwait(false);
                    await FetchAsync(keys, householdId, run, cancel).ConfigureAwait(false);
                }
            }
            store.Problem = null;
        }
        catch (RelayStop stop)
        {
            run.Problem = stop.Problem;
            store.Problem = stop.Problem;
        }
        return run;
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Tells the server what it still has to hear, oldest first, stopping at the first that can't go yet. One the
    /// server won't ever take is dropped, and logged.</summary>
    public async Task FlushAsync(DeviceKeys keys, RelayRun run, CancellationToken cancel)
    {
        while (store.Pending is [var op, ..])
        {
            var result = op.Kind switch
            {
                PendingOp.Create => await relay.CreateHouseholdAsync(keys, op.Household, cancel).ConfigureAwait(false),
                PendingOp.Add when Wire.PublicKey(op.Sign) is { } sign && Wire.PublicKey(op.Dh) is { } dh =>
                    await relay.AddMemberAsync(keys, op.Household, sign, dh, cancel).ConfigureAwait(false),
                PendingOp.Remove when op.Device is { } device =>
                    await relay.RemoveMemberAsync(keys, op.Household, device, cancel).ConfigureAwait(false),
                PendingOp.Keys when op is { Epoch: { } epoch, Envelopes: { } envelopes } =>
                    await relay.PostKeysAsync(keys, op.Household, epoch, envelopes, cancel).ConfigureAwait(false),
                _ => new RelayResult<Done>(400, null, "it wasn't a request this PC can make"),
            };
            if (!result.Ok && result.Transient) throw new RelayStop($"Couldn't reach the server to update the household: {result.Problem}.");
            if (!result.Ok && op is { Kind: PendingOp.Remove } && result.Status == 404)
            {
                // Already gone: removed by another member, or it left.
            }
            else if (!result.Ok && op is { Kind: PendingOp.Keys, Epoch: { } taken } && result.Status == 409)
            {
                await AdoptKeyAsync(keys, op.Household, taken, cancel).ConfigureAwait(false);
            }
            else if (!result.Ok)
            {
                log.LogWarning("The server won't take the household's {Kind} ({Status}: {Problem}); it is dropped", op.Kind, result.Status, result.Problem);
                if (op.Kind == PendingOp.Add && result.Status == 409)
                {
                    run.Notices.Add("The household already has 16 PCs, so the newest one can only sync on the same network.");
                }
            }
            store.Pending = [.. store.Pending.Skip(1)];
        }
    }

    /// <summary>Another member's new key for the same epoch reached the server first: this PC takes that one.</summary>
    private async Task AdoptKeyAsync(DeviceKeys keys, string householdId, int epoch, CancellationToken cancel)
    {
        if (householdId != store.HouseholdId) return;
        if (await FetchKeyAsync(keys, householdId, epoch, cancel).ConfigureAwait(false) is { } key) store.AddKey(epoch, key, replace: true);
    }

    /// <summary>Reads the member list for who has gone: members the server says were removed are marked as left, and this PC
    /// itself, when it was removed, leaves. A PC gone means a new key (households design §6): the next epochs' envelopes are
    /// looked for, as nothing else says there is one until a batch under it comes.</summary>
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
            if (household.Member(member.Device) is { LeftMs: null } known)
            {
                household.MarkLeft(member.Device, removed);
                run.Notices.Add($"{known.Name} is no longer in the household.");
                gone++;
            }
        }
        for (var probe = 0; gone > 0 && probe < 3; probe++)
        {
            var epoch = store.Epoch + 1;
            if (await FetchKeyAsync(keys, householdId, epoch, cancel).ConfigureAwait(false) is not { } key) break;
            store.AddKey(epoch, key);
        }
    }

    private void WasRemoved(RelayRun run)
    {
        log.LogInformation("This PC was removed from its household");
        Membership.Forget(store, household, Now);
        run.Removed = true;
        run.Notices.Add("This PC was removed from the household.");
    }

    /// <summary>A refusal from a household route: a PC the server has taken as a member before that is refused now was
    /// removed; one it never took hasn't been added yet, and waits quietly.</summary>
    /// <returns>True for an answer to go on with.</returns>
    private bool Check<T>(RelayResult<T> result, RelayRun run)
    {
        if (result.Ok) return true;
        if (result.Status == 403)
        {
            if (store.RelayConfirmed)
            {
                WasRemoved(run);
                return false;
            }
            throw new RelayStop(null);
        }
        if (result.Transient) throw new RelayStop($"Couldn't sync through the server: {result.Problem}.");
        log.LogWarning("The server refused a household request ({Status}: {Problem})", result.Status, result.Problem);
        throw new RelayStop($"The server refused to sync: {result.Problem}.");
    }

    private async Task PostNewRowsAsync(DeviceKeys keys, string householdId, string name, ChassisKind kind, RelayRun run, CancellationToken cancel)
    {
        var rows = household.ChangedAfter(keys.DeviceId, store.PostedThrough);
        if (rows.Count == 0) return;
        await PostRowsAsync(keys, householdId, name, kind, rows, run, cancel).ConfigureAwait(false);
        store.PostedThrough = rows.Max(row => row.ChangedMs);
    }

    /// <summary>This PC's last 13 months of rows, once, for each member it hasn't posted them for (households design §5).</summary>
    private async Task PostHistoryAsync(DeviceKeys keys, string householdId, string name, ChassisKind kind, RelayRun run, CancellationToken cancel)
    {
        var posted = store.HistoryPosted;
        var newcomers = household.Members()
            .Where(member => member.LeftMs is null && member.DeviceId != keys.DeviceId && !posted.Contains(member.DeviceId))
            .Select(member => member.DeviceId)
            .ToList();
        if (newcomers.Count == 0) return;
        var now = clock.GetUtcNow();
        var rows = household.RowsBetween(keys.DeviceId, HourRows.BackfillFrom(now).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
        if (rows.Count > 0) await PostRowsAsync(keys, householdId, name, kind, rows, run, cancel).ConfigureAwait(false);
        store.HistoryPosted = [.. posted, .. newcomers];
    }

    /// <summary>Posts the rows as batches under the current key, each at most 1 MB as posted: a batch that would be larger
    /// is split in two until it fits.</summary>
    private async Task PostRowsAsync(
        DeviceKeys keys, string householdId, string name, ChassisKind kind, List<HouseholdRow> rows, RelayRun run, CancellationToken cancel)
    {
        var epoch = store.Epoch;
        var key = store.CurrentKey ?? throw new RelayStop(null);
        var device = new WireMember(keys.DeviceId, name, Wire.Kind(kind), Wire.Encode(keys.SignPublic), Wire.Encode(keys.DhPublic));
        var start = 0;
        var size = FirstChunk;
        while (start < rows.Count)
        {
            var take = Math.Min(size, rows.Count - start);
            var plain = HouseholdJson.Bytes(new BatchPlain(1, device, [.. rows.Skip(start).Take(take).Select(Wire.Row)]), HouseholdJson.Default.BatchPlain);
            var packed = SharingClient.Gzip(plain);
            if (PostedSize(packed.Length) > MaxPostBytes && take > 1)
            {
                size = take / 2;
                continue;
            }
            var seq = store.NextSequence();
            var sealedBody = HouseholdCrypto.Seal(key, packed, HouseholdCrypto.BatchAad(householdId, keys.DeviceId, epoch, seq));
            var result = await relay.PostBatchAsync(keys, householdId, new BatchPost(keys.DeviceId, epoch, seq, Wire.Encode(sealedBody)), cancel)
                .ConfigureAwait(false);
            if (!Check(result, run)) throw new RelayStop(null);
            store.RelayConfirmed = true;
            run.RowsOut += take;
            start += take;
        }
    }

    /// <summary>The size of a batch as posted: the sealed body in base64url and the JSON around it.</summary>
    private static long PostedSize(int packed) => (packed + 28 + 2) / 3 * 4 + 256;

    /// <summary>Reads the other members' batches after the cursor, a page at a time, moving the cursor past each page once
    /// it is kept. A batch under a newer epoch whose key can't be had yet holds the cursor where it is, so the page is read
    /// again next time, what was kept from it being kept again; after <see cref="KeyWait"/> such a batch is passed over.</summary>
    private async Task FetchAsync(DeviceKeys keys, string householdId, RelayRun run, CancellationToken cancel)
    {
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await relay.BatchesAsync(keys, householdId, store.RelayCursor, PageLimit, cancel).ConfigureAwait(false);
            if (!Check(result, run)) return;
            store.RelayConfirmed = true;
            var giveUp = store.WaitingSince is { } since && Now - since >= (long)KeyWait.TotalMilliseconds;
            foreach (var item in result.Value!.Items ?? [])
            {
                var opened = await OpenAsync(keys, householdId, item, run, cancel).ConfigureAwait(false);
                if (run.Removed) return;
                if (opened is null && !giveUp)
                {
                    store.WaitingSince ??= Now;
                    return;
                }
                run.RowsIn += opened ?? 0;
            }
            store.WaitingSince = null;
            store.RelayCursor = Math.Max(store.RelayCursor, result.Value.Next);
            if (!result.Value.More) return;
        }
    }

    /// <summary>Opens one batch and keeps its rows; one that doesn't open, or isn't from a member, is passed over. A newer
    /// epoch than this PC's sends it to the member list first, for who was removed, then for its envelope.</summary>
    /// <returns>How many rows were newer than those kept; null when the batch is under a newer epoch whose key this PC
    /// can't have yet.</returns>
    private async Task<int?> OpenAsync(DeviceKeys keys, string householdId, BatchItem item, RelayRun run, CancellationToken cancel)
    {
        if (!Wire.IsDeviceId(item.Device) || item.Device == keys.DeviceId || item.Epoch <= 0) return 0;
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
        if (key is null || Wire.Decode(item.Body) is not { } sealedBody) return 0;
        BatchPlain? batch;
        try
        {
            var packed = HouseholdCrypto.Open(key, sealedBody, HouseholdCrypto.BatchAad(householdId, item.Device, item.Epoch, item.Seq));
            batch = JsonSerializer.Deserialize(Gunzip(packed), HouseholdJson.Default.BatchPlain);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or JsonException or IOException)
        {
            log.LogWarning("A batch from {Device} didn't open, so it was passed over", item.Device);
            return 0;
        }
        if (batch?.Device is not { } sender || sender.Id != item.Device) return 0;

        var known = household.Member(item.Device);
        if (known is null)
        {
            // A PC this one hasn't heard of: taken only from a batch under the current key, with keys that make its ID.
            if (item.Epoch < store.Epoch || Wire.Member(sender) is not { } newcomer) return 0;
            household.SaveMember(new HouseholdMember(newcomer.Id, newcomer.Name, newcomer.Kind, newcomer.Sign, newcomer.Dh, Now, null, null));
        }
        else if (Wire.Name(sender.Name) is { } name && Wire.Kind(sender.Kind) is { } kind && (name != known.Name || kind != known.Kind))
        {
            household.SaveMember(known with { Name = name, Kind = kind });
        }
        var rows = (batch.Rows ?? []).Select(row => Wire.Row(item.Device, row)).OfType<HouseholdRow>().ToList();
        var taken = household.Upsert(rows);
        if (rows.Count > 0) household.Synced(item.Device, Math.Min(Now, rows.Max(row => row.ChangedMs)));
        return taken;
    }

    /// <summary>This PC's envelope for an epoch, opened with the key of the member that sealed it; null when there is none.</summary>
    private async Task<byte[]?> FetchKeyAsync(DeviceKeys keys, string householdId, int epoch, CancellationToken cancel)
    {
        var result = await relay.GetKeyAsync(keys, householdId, epoch, cancel).ConfigureAwait(false);
        if (!result.Ok)
        {
            if (result.Transient) throw new RelayStop($"Couldn't sync through the server: {result.Problem}.");
            return null;
        }
        var reply = result.Value!;
        if (reply.Epoch != epoch || household.Member(reply.From) is not { } sealer) return null;
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
