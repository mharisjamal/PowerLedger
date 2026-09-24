using System.Security.Cryptography;
using System.Text;
using PowerLedger.Core.Households;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households.Lan;

/// <summary>How a sync on the network went.</summary>
/// <param name="PeerId">The other PC's device ID, once it proved it holds that key.</param>
/// <param name="RowsIn">Rows the other PC sent that were newer than those kept here.</param>
/// <param name="RowsOut">Rows sent to the other PC.</param>
internal sealed record SyncOutcome(bool Ok, string? PeerId, int RowsIn, int RowsOut, string? Problem = null);

/// <summary>
/// Sync on the same network (households design §5, plan 0.6). After the hellos and the key exchange each side proves its
/// device key by signing <c>"sync" ‖ eph_a ‖ eph_b</c>, which only a member's key checks against; then each says what it
/// has, as the newest change it holds of each member's rows and the latest hour among the rows changed then, and sends
/// the rows the other lacks in that order, then done: a sync cut short goes on from the row after the last that came. The side that
/// connected sends first each time, so the two never both wait to write. The <c>have</c> also carries the members each
/// knows, so a PC added elsewhere is learned of here; only a member's own entry changes its name.
/// </summary>
internal sealed class LanSync(HouseholdRepository household, TimeProvider clock, PairingTimeouts? timeouts = null)
{
    /// <summary>Rows in one frame: about 350 bytes each, well inside the 1 MB frame.</summary>
    public const int RowsPerFrame = 2000;

    private readonly TimeSpan _step = (timeouts ?? PairingTimeouts.Default).Step;

    /// <summary>The side that connected to a member it found on the network.</summary>
    public async Task<SyncOutcome> SyncAsync(IFrameChannel channel, PairingIdentity me, CancellationToken cancel)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var talk = new LanConversation(channel, _step);
        try
        {
            var myHello = LanMessages.Write(LanMessages.Hello(Hello.Sync, ephPublic, me.Keys, me.Name, me.Kind, me.Instance));
            await talk.SendAsync(myHello, cancel).ConfigureAwait(false);
            var (theirMessage, theirHello) = await talk.ReceiveWithBytesAsync("hello", cancel).ConfigureAwait(false);
            if (Hello.Of(theirMessage) is not { Purpose: Hello.Sync } hello) return Refused("its hello wasn't a good one");
            if (Member(hello) is null) return Refused("it isn't in this household");
            using var cipher = FrameCipher.For(adder: true, HouseholdCrypto.Agree(eph, hello.Eph), HouseholdCrypto.Transcript(myHello, theirHello));
            talk.Secure(cipher);
            var proof = Proof(ephPublic, hello.Eph);

            await talk.SendAsync(Prove(me, proof), cancel).ConfigureAwait(false);
            await CheckProofAsync(talk, hello, proof, cancel).ConfigureAwait(false);
            await talk.SendAsync(Have(), cancel).ConfigureAwait(false);
            var theirs = await talk.ReceiveAsync("have", cancel).ConfigureAwait(false);
            Learn(theirs, hello.From.Id);
            var sent = await SendRowsAsync(talk, theirs, cancel).ConfigureAwait(false);
            var received = await ReceiveRowsAsync(talk, hello.From.Id, cancel).ConfigureAwait(false);
            return new SyncOutcome(true, hello.From.Id, received, sent);
        }
        catch (LanException error)
        {
            return Refused(error.Problem.ToString());
        }
    }

    /// <summary>The side that was connected to and has read the other's hello, <paramref name="hello"/> as it came.</summary>
    public async Task<SyncOutcome> RespondAsync(IFrameChannel channel, byte[] hello, PairingIdentity me, CancellationToken cancel)
    {
        if (Hello.Of(LanMessages.Read(hello)) is not { Purpose: Hello.Sync } theirHello) return Refused("its hello wasn't a good one");
        if (Member(theirHello) is null) return Refused("it isn't in this household");
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var talk = new LanConversation(channel, _step);
        try
        {
            var myHello = LanMessages.Write(LanMessages.Hello(Hello.Sync, ephPublic, me.Keys, me.Name, me.Kind, me.Instance));
            await talk.SendAsync(myHello, cancel).ConfigureAwait(false);
            using var cipher = FrameCipher.For(adder: false, HouseholdCrypto.Agree(eph, theirHello.Eph), HouseholdCrypto.Transcript(hello, myHello));
            talk.Secure(cipher);
            var proof = Proof(theirHello.Eph, ephPublic);

            await CheckProofAsync(talk, theirHello, proof, cancel).ConfigureAwait(false);
            await talk.SendAsync(Prove(me, proof), cancel).ConfigureAwait(false);
            var theirs = await talk.ReceiveAsync("have", cancel).ConfigureAwait(false);
            Learn(theirs, theirHello.From.Id);
            await talk.SendAsync(Have(), cancel).ConfigureAwait(false);
            var received = await ReceiveRowsAsync(talk, theirHello.From.Id, cancel).ConfigureAwait(false);
            var sent = await SendRowsAsync(talk, theirs, cancel).ConfigureAwait(false);
            return new SyncOutcome(true, theirHello.From.Id, received, sent);
        }
        catch (LanException error)
        {
            return Refused(error.Problem.ToString());
        }
    }

    /// <summary>What both sides sign: "sync", then the connecting side's ephemeral key, then the other's.</summary>
    private static byte[] Proof(byte[] ephA, byte[] ephB) => [.. Encoding.ASCII.GetBytes("sync"), .. ephA, .. ephB];

    private static LanMessage Prove(PairingIdentity me, byte[] proof) =>
        new() { Type = "prove", Sig = Wire.Encode(HouseholdCrypto.SignData(me.Keys.Sign, proof)) };

    /// <summary>The other side's prove must be its member key's signature over this session's proof.</summary>
    private async Task CheckProofAsync(LanConversation talk, Hello hello, byte[] proof, CancellationToken cancel)
    {
        var prove = await talk.ReceiveAsync("prove", cancel).ConfigureAwait(false);
        if (Member(hello) is not { } member || Wire.Decode(prove.Sig) is not { } signature || !HouseholdCrypto.Verify(member.SignKey, proof, signature))
        {
            throw new LanException(LanProblem.NotAMember);
        }
    }

    /// <summary>The current member the hello's keys belong to, or null.</summary>
    private HouseholdMember? Member(Hello hello) =>
        household.Member(hello.From.Id) is { LeftMs: null } member && member.SignKey.AsSpan().SequenceEqual(hello.From.Sign) ? member : null;

    private LanMessage Have()
    {
        var members = household.Members().Where(member => member.LeftMs is null).ToList();
        var reach = household.Reach();
        return new LanMessage
        {
            Type = "have",
            Latest = members.ToDictionary(member => member.DeviceId, member => reach.GetValueOrDefault(member.DeviceId).Changed),
            Hours = members.Where(member => reach.ContainsKey(member.DeviceId)).ToDictionary(member => member.DeviceId, member => reach[member.DeviceId].Hour),
            Members = [.. members.Select(Wire.Member)],
        };
    }

    /// <summary>Adds the members the other side knows and this one doesn't, and takes the other's own name and kind. A member
    /// this PC knows has left stays left: leaving is the leaver's, or the remover's, to say.</summary>
    private void Learn(LanMessage have, string peerId)
    {
        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var sent in (have.Members ?? []).Take(Wire.MaxMembers))
        {
            if (Wire.Member(sent) is not { } member) continue;
            var known = household.Member(member.Id);
            if (known is null)
            {
                household.SaveMember(new HouseholdMember(member.Id, member.Name, member.Kind, member.Sign, member.Dh, nowMs, null, null));
            }
            else if (member.Id == peerId && known.LeftMs is null && (known.Name != member.Name || known.Kind != member.Kind))
            {
                household.SaveMember(known with { Name = member.Name, Kind = member.Kind });
            }
        }
    }

    /// <summary>Sends every current member's rows after the last the other side has of them, in the order they changed, then
    /// done.</summary>
    private async Task<int> SendRowsAsync(LanConversation talk, LanMessage theirs, CancellationToken cancel)
    {
        var sent = 0;
        foreach (var member in household.Members().Where(member => member.LeftMs is null))
        {
            var after = theirs.Latest?.GetValueOrDefault(member.DeviceId) ?? 0;
            var rows = theirs.Hours?.TryGetValue(member.DeviceId, out var hour) == true
                ? household.ChangedAfter(member.DeviceId, after, hour)
                : household.ChangedAfter(member.DeviceId, after);
            foreach (var chunk in rows.Chunk(RowsPerFrame))
            {
                await talk.SendAsync(new LanMessage { Type = "rows", Device = member.DeviceId, Rows = [.. chunk.Select(Wire.Row)] }, cancel)
                    .ConfigureAwait(false);
                sent += chunk.Length;
            }
        }
        await talk.SendAsync(new LanMessage { Type = "done" }, cancel).ConfigureAwait(false);
        return sent;
    }

    /// <summary>Takes rows until done: each for a current member, checked, and kept when newer than the one here. The PC
    /// synced with was heard from now; any other member as of the newest change among its rows.</summary>
    private async Task<int> ReceiveRowsAsync(LanConversation talk, string peerId, CancellationToken cancel)
    {
        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var taken = 0;
        while (true)
        {
            var message = await talk.ReceiveAnyAsync(cancel).ConfigureAwait(false);
            if (message.Type == "done") break;
            if (message.Type != "rows") throw new LanException(LanProblem.Broken);
            if (message.Device is not { } device || household.Member(device) is not { LeftMs: null }) continue;
            var rows = (message.Rows ?? []).Select(row => Wire.Row(device, row)).OfType<HouseholdRow>().ToList();
            taken += household.Upsert(rows);
            if (rows.Count > 0 && device != peerId) household.Synced(device, Math.Min(nowMs, rows.Max(row => row.ChangedMs)));
        }
        household.Synced(peerId, nowMs);
        return taken;
    }

    private static SyncOutcome Refused(string problem) => new(false, null, 0, 0, problem);
}
