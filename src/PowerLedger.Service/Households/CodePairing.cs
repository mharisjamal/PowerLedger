using System.Security.Cryptography;
using System.Text;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Service.Households.Relay;

namespace PowerLedger.Service.Households;

/// <summary>A code made for pairing and the adder's half of its meeting, until it is used or runs out.</summary>
internal sealed class CodeMeeting(string code, string normalized, ECDiffieHellman eph, DateTimeOffset opened) : IDisposable
{
    /// <summary>As shown: <c>K7QM-2XHD-9PW4-R8TA</c>.</summary>
    public string Code { get; } = code;

    public string Normalized { get; } = normalized;

    public string MeetingId { get; } = PairingCode.MeetingId(normalized);

    public byte[] CodeKey { get; } = PairingCode.Key(normalized);

    public ECDiffieHellman Eph { get; } = eph;

    public byte[] EphPublic { get; } = eph.ExportSubjectPublicKeyInfo();

    public DateTimeOffset Opened { get; } = opened;

    public void Dispose() => Eph.Dispose();
}

/// <summary>
/// Pairing through the server with a one-time code (households design §4, plan 0.6), for a PC somewhere else. The adding PC
/// makes a code of 80 random bits and puts its keys in the meeting's <c>adder</c> slot; the joining PC, given the code,
/// reads them and puts its own in <c>joiner</c>. Each carries a MAC under a key made from the code, so the server, which
/// only sees the meeting ID made from the code, can't put in keys of its own; and nothing else, so the server never sees a
/// PC's name (plan 0.8). Both then agree a secret from their ephemeral keys; the joining PC's user is asked, without a
/// comparison code since the code vouches for the adder, and the answer with the joining PC's name, the welcome with the
/// adding PC's, and the joining PC's proof of its join go through the <c>answer</c>, <c>welcome</c> and <c>joined</c>
/// slots, sealed. The joining PC enters the household only once the adding PC, having recorded it, writes the sealed
/// <c>welcomed</c> slot (plan 0.8); without it, nothing changes on the joining PC. A meeting lasts 10 minutes.
/// </summary>
/// <param name="wait">How a poll waits for the next; <see cref="PollEvery"/> on the clock when null.</param>
internal sealed class CodePairing(RelayClient relay, TimeProvider clock, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How often a slot is looked at while waiting: the Worker lets an address make 60 requests a minute.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

    private const string Side = "powerledger code ";
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? ((delay, cancel) => Task.Delay(delay, clock, cancel));

    /// <summary>The adding side's first step: a new code, with this PC's hello in its meeting.</summary>
    /// <returns>Null when the server couldn't take it.</returns>
    public async Task<CodeMeeting?> OpenAsync(PairingIdentity me, CancellationToken cancel)
    {
        var code = PairingCode.New();
        var meeting = new CodeMeeting(code, PairingCode.Normalize(code)!, ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), clock.GetUtcNow());
        var put = await relay.PutSlotAsync(meeting.MeetingId, "adder", Keys(meeting.EphPublic, me, meeting.CodeKey, "adder"), cancel).ConfigureAwait(false);
        if (put.Ok) return meeting;
        meeting.Dispose();
        return null;
    }

    /// <summary>The adding side from there: waits for a joining PC's hello and checks its MAC, then its answer, then sends
    /// the welcome, and once the joining PC's proof comes, records it and says so, all within the meeting's 10 minutes.</summary>
    /// <param name="record">Records the joining PC as a member, with its proof for the server, before it is told it is in.</param>
    public async Task<PairingOutcome> AddAsync(
        CodeMeeting meeting, PairingIdentity me, Func<MemberInfo, Task<Welcome>> welcomeFor, Func<MemberInfo, byte[], Task> record,
        CancellationToken cancel)
    {
        var deadline = meeting.Opened + Lifetime;
        var slot = await PollAsync(meeting.MeetingId, "joiner", deadline, cancel).ConfigureAwait(false);
        if (slot is null) return new PairingOutcome.Failed("The code ran out before another PC used it.");
        if (Checked(slot, meeting.CodeKey, "joiner") is not { } keys)
        {
            return new PairingOutcome.Failed("A PC tried the code, but its keys didn't match it, so it wasn't added.");
        }
        if (keys.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That is this PC.");
        var (toJoiner, toAdder) = SessionKeys(meeting.Eph, keys.Eph, meeting.EphPublic, keys.Eph);

        var sealedAnswer = await PollAsync(meeting.MeetingId, "answer", deadline, cancel).ConfigureAwait(false);
        if (sealedAnswer is null) return new PairingOutcome.Failed("The other PC didn't answer before the code ran out.");
        var answer = Open(toAdder, sealedAnswer, "answer");
        if (answer is not { Type: "answer" }) return new PairingOutcome.Failed("The other PC's answer didn't open, so it wasn't added.");
        if (answer.Accept != true) return new PairingOutcome.Refused("The other PC didn't join.");
        if (Wire.Name(answer.Name) is not { } name || Wire.Kind(answer.Kind) is not { } kind)
        {
            return new PairingOutcome.Failed("The other PC's answer wasn't a good one, so it wasn't added.");
        }
        var joiner = new MemberInfo(keys.Id, name, kind, keys.Sign, keys.Dh);

        var welcome = await welcomeFor(joiner).ConfigureAwait(false);
        var sealedWelcome = HouseholdCrypto.Seal(toJoiner, LanMessages.Write(PairingSession.WelcomeMessage(welcome)), Encoding.ASCII.GetBytes("welcome"));
        var put = await relay.PutSlotAsync(meeting.MeetingId, "welcome", sealedWelcome, cancel).ConfigureAwait(false);
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't give {name} the household: {put.Problem}.");

        var sealedProof = await PollAsync(meeting.MeetingId, "joined", deadline, cancel).ConfigureAwait(false);
        if (sealedProof is null) return new PairingOutcome.Failed($"{name} didn't finish joining before the code ran out.");
        var proof = OpenBytes(toAdder, sealedProof, "joined");
        if (!Wire.IsJoinProof(joiner, welcome.HouseholdId, proof))
        {
            return new PairingOutcome.Failed($"{name} didn't sign its joining, so it wasn't added.");
        }
        await record(joiner, proof!).ConfigureAwait(false);
        var welcomed = HouseholdCrypto.Seal(toJoiner, LanMessages.Write(new LanMessage { Type = "welcomed" }), Encoding.ASCII.GetBytes("welcomed"));
        if (!await PutUntilAsync(meeting.MeetingId, "welcomed", welcomed, deadline, cancel).ConfigureAwait(false))
        {
            return new PairingOutcome.Failed($"Couldn't tell {name} it was added. If it doesn't show your household, remove it here and add it again.");
        }
        return new PairingOutcome.Joined(joiner, $"{name} joined your household.", proof);
    }

    /// <summary>The joining side, given the code as the user typed it.</summary>
    /// <param name="inHousehold">True when this PC is in a household that joining leaves: the user is told so.</param>
    /// <param name="enter">Takes this PC into the household in the welcome, once the adding PC has said it recorded the joining.</param>
    public async Task<PairingOutcome> JoinAsync(
        string typed, PairingIdentity me, IPromptBroker broker, bool inHousehold, Func<Welcome, MemberInfo, Task> enter, CancellationToken cancel)
    {
        if (PairingCode.Normalize(typed) is not { } normalized)
        {
            return new PairingOutcome.Failed("That isn't a code. A code has 16 letters and digits, like K7QM-2XHD-9PW4-R8TA.");
        }
        var meetingId = PairingCode.MeetingId(normalized);
        var codeKey = PairingCode.Key(normalized);
        var found = await relay.GetSlotAsync(meetingId, "adder", cancel).ConfigureAwait(false);
        if (found.Status == 404) return new PairingOutcome.Failed("No PC is waiting with that code. Check it, or make a new one on the other PC.");
        if (!found.Ok) return new PairingOutcome.Failed($"Couldn't reach the server: {found.Problem}.");
        if (Checked(found.Value!, codeKey, "adder") is not { } adder)
        {
            return new PairingOutcome.Failed("That code doesn't match the other PC's, so nothing was changed.");
        }
        if (adder.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That code was made on this PC. Type it on the other one.");

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var put = await relay.PutSlotAsync(meetingId, "joiner", Keys(ephPublic, me, codeKey, "joiner"), cancel).ConfigureAwait(false);
        if (put.Status == 409) return new PairingOutcome.Failed("Another PC has already used that code.");
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't reach the server: {put.Problem}.");
        var (toJoiner, toAdder) = SessionKeys(eph, adder.Eph, adder.Eph, ephPublic);
        var deadline = clock.GetUtcNow() + Lifetime;

        var accept = await broker.AskToJoinAsync(new JoinQuestion(null, null, inHousehold), cancel).ConfigureAwait(false);
        var answer = HouseholdCrypto.Seal(toAdder, LanMessages.Write(new LanMessage
        {
            Type = "answer", Accept = accept, Name = me.Name, Kind = Wire.Kind(me.Kind), Instance = me.Instance,
        }), Encoding.ASCII.GetBytes("answer"));
        put = await relay.PutSlotAsync(meetingId, "answer", answer, cancel).ConfigureAwait(false);
        if (!accept) return new PairingOutcome.Refused("This PC didn't join the other PC's household.");
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't give the other PC the answer: {put.Problem}.");

        var sealedWelcome = await PollAsync(meetingId, "welcome", deadline, cancel).ConfigureAwait(false);
        if (sealedWelcome is null) return new PairingOutcome.Failed("The other PC didn't finish adding this PC in time, so nothing was changed.");
        var keysOnly = new MemberInfo(adder.Id, "", ChassisKind.Desktop, adder.Sign, adder.Dh);
        if (Open(toJoiner, sealedWelcome, "welcome") is not { Type: "welcome" } message || PairingSession.ReadWelcome(message, keysOnly) is not { } welcome)
        {
            return new PairingOutcome.Failed("The other PC sent a household that wasn't a good one, so nothing was changed.");
        }
        var from = welcome.Members.First(member => member.Id == adder.Id);
        var joined = HouseholdCrypto.Seal(toAdder, Wire.SignJoin(me.Keys, welcome.HouseholdId), Encoding.ASCII.GetBytes("joined"));
        put = await relay.PutSlotAsync(meetingId, "joined", joined, cancel).ConfigureAwait(false);
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't tell {from.Name} this PC is joining: {put.Problem}. Nothing was changed.");
        var sealedWelcomed = await PollAsync(meetingId, "welcomed", deadline, cancel).ConfigureAwait(false);
        if (sealedWelcomed is null || Open(toJoiner, sealedWelcomed, "welcomed") is not { Type: "welcomed" })
        {
            return new PairingOutcome.Failed($"{from.Name} didn't finish adding this PC in time, so nothing was changed.");
        }
        await enter(welcome, from).ConfigureAwait(false);
        return new PairingOutcome.Joined(from, $"This PC joined {from.Name}'s household.");
    }

    /// <summary>A PC's keys as its meeting slot carries them: its ephemeral key, its device keys and the MAC over them under
    /// the code's key; no name, kind or instance, which go sealed.</summary>
    private static byte[] Keys(byte[] ephPublic, PairingIdentity me, byte[] codeKey, string side) => LanMessages.Write(new LanMessage
    {
        Type = "hello",
        V = LanMessages.Version,
        Purpose = Lan.Hello.Pair,
        Eph = Wire.Encode(ephPublic),
        Sign = Wire.Encode(me.Keys.SignPublic),
        Dh = Wire.Encode(me.Keys.DhPublic),
        Mac = Wire.Encode(PairingCode.Mac(codeKey, side, ephPublic, me.Keys.SignPublic, me.Keys.DhPublic)),
    });

    /// <summary>The keys in a slot, when its MAC under the code's key is right for <paramref name="side"/>.</summary>
    private static SlotKeys? Checked(byte[] slot, byte[] codeKey, string side)
    {
        if (LanMessages.Read(slot) is not { Type: "hello", V: LanMessages.Version, Purpose: Lan.Hello.Pair } message) return null;
        if (Wire.PublicKey(message.Eph) is not { } eph || Wire.PublicKey(message.Sign) is not { } sign || Wire.PublicKey(message.Dh) is not { } dh
            || Wire.Decode(message.Mac) is not { } mac)
        {
            return null;
        }
        var expected = PairingCode.Mac(codeKey, side, eph, sign, dh);
        return CryptographicOperations.FixedTimeEquals(mac, expected) ? new SlotKeys(HouseholdCrypto.DeviceIdOf(sign), eph, sign, dh) : null;
    }

    private sealed record SlotKeys(string Id, byte[] Eph, byte[] Sign, byte[] Dh);

    /// <summary>The keys each way, from the ephemeral keys' shared secret, the adder's key first.</summary>
    private static (byte[] ToJoiner, byte[] ToAdder) SessionKeys(ECDiffieHellman mine, byte[] theirs, byte[] ephAdder, byte[] ephJoiner)
    {
        var shared = HouseholdCrypto.Agree(mine, theirs);
        byte[] salt = [.. ephAdder, .. ephJoiner];
        return (HouseholdCrypto.Hkdf(shared, salt, Side + "a2j"), HouseholdCrypto.Hkdf(shared, salt, Side + "j2a"));
    }

    private static LanMessage? Open(byte[] key, byte[] sealedBytes, string slot) =>
        OpenBytes(key, sealedBytes, slot) is { } plaintext ? LanMessages.Read(plaintext) : null;

    private static byte[]? OpenBytes(byte[] key, byte[] sealedBytes, string slot)
    {
        try
        {
            return HouseholdCrypto.Open(key, sealedBytes, Encoding.ASCII.GetBytes(slot));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Writes a slot, trying again while the server can't be reached, until the deadline; one already written
    /// counts as written.</summary>
    /// <returns>False when it couldn't be written in time.</returns>
    private async Task<bool> PutUntilAsync(string meetingId, string slot, byte[] body, DateTimeOffset deadline, CancellationToken cancel)
    {
        while (true)
        {
            var put = await relay.PutSlotAsync(meetingId, slot, body, cancel).ConfigureAwait(false);
            if (put.Ok || put.Status == 409) return true;
            if (!put.Transient || clock.GetUtcNow() >= deadline) return false;
            await _wait(PollEvery, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>A slot's bytes once they are there; null when the deadline passes first. A refusal or no answer from the
    /// server is waited out like an empty slot.</summary>
    private async Task<byte[]?> PollAsync(string meetingId, string slot, DateTimeOffset deadline, CancellationToken cancel)
    {
        while (clock.GetUtcNow() < deadline)
        {
            var got = await relay.GetSlotAsync(meetingId, slot, cancel).ConfigureAwait(false);
            if (got.Ok && got.Value is { Length: > 0 } bytes) return bytes;
            await _wait(PollEvery, cancel).ConfigureAwait(false);
        }
        return null;
    }
}
