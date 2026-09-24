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
/// <c>welcomed</c> slot (plan 0.8); without it, nothing changes on the joining PC. A meeting lasts 10 minutes. A code its
/// adding PC stopped before any PC used it says so in the <c>welcome</c> slot, sealed under a key from the code, which the
/// joining PC looks at before it asks its user (plan 0.10).
/// </summary>
/// <param name="wait">How a poll waits for the next; <see cref="PollEvery"/> on the clock when null.</param>
internal sealed class CodePairing(RelayClient relay, TimeProvider clock, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How often a slot is looked at while waiting: the Worker lets an address make 60 requests a minute.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

    /// <summary>The most a meeting slot holds (the Worker's limit), less room for sealing.</summary>
    internal const int MaxWelcome = 8 * 1024 - 64;

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
    /// the welcome, and once the joining PC's proof comes, records it and says so, all within the meeting's 10 minutes.
    /// Cancelled, it tells the joining PC so in the next slot it would have written, as far as it has got.</summary>
    /// <param name="welcomeFor">Makes the welcome for the joining PC; null when it can't be added yet.</param>
    /// <param name="record">Records the joining PC as a member of the welcome's household, with its proof for the server, in
    /// one step before it is told it is in (plan 0.9); what went wrong when it couldn't, else null. A cancel before the step
    /// leaves nothing behind; after it, the pairing completes.</param>
    public async Task<PairingOutcome> AddAsync(
        CodeMeeting meeting, PairingIdentity me, Func<MemberInfo, Task<Welcome?>> welcomeFor,
        Func<Welcome, MemberInfo, byte[], CancellationToken, Task<string?>> record, CancellationToken cancel)
    {
        var deadline = meeting.Opened + Lifetime;
        (byte[] Key, string Slot)? goodbye = null;
        try
        {
            var slot = await PollAsync(meeting.MeetingId, "joiner", deadline, cancel).ConfigureAwait(false);
            if (slot is null) return new PairingOutcome.Failed("The code ran out before another PC used it.");
            if (Checked(slot, meeting.CodeKey, "joiner") is not { } keys)
            {
                return new PairingOutcome.Failed("A PC tried the code, but its keys didn't match it, so it wasn't added.");
            }
            if (keys.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That is this PC.");
            var (toJoiner, toAdder) = SessionKeys(meeting.Eph, keys.Eph, meeting.EphPublic, keys.Eph);
            goodbye = (toJoiner, "welcome");

            var sealedAnswer = await PollAsync(meeting.MeetingId, "answer", deadline, cancel).ConfigureAwait(false);
            if (sealedAnswer is null) return new PairingOutcome.Failed("The other PC didn't answer before the code ran out.");
            var answer = Open(toAdder, sealedAnswer, "answer");
            if (answer is { Type: "cancel" }) return new PairingOutcome.Refused("The other PC stopped the pairing.");
            if (answer is not { Type: "answer" }) return new PairingOutcome.Failed("The other PC's answer didn't open, so it wasn't added.");
            if (answer.Accept != true) return new PairingOutcome.Refused("The other PC didn't join.");
            if (Wire.Name(answer.Name) is not { } name || Wire.Kind(answer.Kind) is not { } kind)
            {
                return new PairingOutcome.Failed("The other PC's answer wasn't a good one, so it wasn't added.");
            }
            var joiner = new MemberInfo(keys.Id, name, kind, keys.Sign, keys.Dh);

            if (await welcomeFor(joiner).ConfigureAwait(false) is not { } welcome)
            {
                await GoodbyeAsync(meeting.MeetingId, "welcome", toJoiner).ConfigureAwait(false);
                return new PairingOutcome.Failed(PairingSession.NotYet(name));
            }
            var sealedWelcome = HouseholdCrypto.Seal(
                toJoiner, LanMessages.Write(PairingSession.WelcomeMessage(welcome, MaxWelcome)), Encoding.ASCII.GetBytes("welcome"));
            var put = await relay.PutSlotAsync(meeting.MeetingId, "welcome", sealedWelcome, cancel).ConfigureAwait(false);
            if (!put.Ok) return new PairingOutcome.Failed($"Couldn't give {name} the household: {put.Problem}.");
            goodbye = (toJoiner, "welcomed");

            var sealedProof = await PollAsync(meeting.MeetingId, "joined", deadline, cancel).ConfigureAwait(false);
            if (sealedProof is null) return new PairingOutcome.Failed($"{name} didn't finish joining before the code ran out.");
            var proof = OpenBytes(toAdder, sealedProof, "joined");
            if (proof is not null && LanMessages.Read(proof) is { Type: "cancel" }) return new PairingOutcome.Refused($"{name} stopped the pairing.");
            if (!Wire.IsJoinProof(joiner, welcome.HouseholdId, proof))
            {
                return new PairingOutcome.Failed($"{name} didn't sign its joining, so it wasn't added.");
            }
            if (await record(welcome, joiner, proof!, cancel).ConfigureAwait(false) is { } refusal)
            {
                await GoodbyeAsync(meeting.MeetingId, "welcomed", toJoiner).ConfigureAwait(false);
                return new PairingOutcome.Failed(refusal);
            }
            goodbye = null;
            var welcomed = HouseholdCrypto.Seal(toJoiner, LanMessages.Write(new LanMessage { Type = "welcomed" }), Encoding.ASCII.GetBytes("welcomed"));
            if (!await PutUntilAsync(meeting.MeetingId, "welcomed", welcomed, deadline, CancellationToken.None).ConfigureAwait(false))   // recorded: no cancel now
            {
                return new PairingOutcome.Failed($"Couldn't tell {name} it was added. If it doesn't show your household, remove it here and add it again.");
            }
            return new PairingOutcome.Joined(joiner, $"{name} joined your household.", proof);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            goodbye ??= await JoinerNowAsync(meeting).ConfigureAwait(false) is { } toJoiner ? (toJoiner, "welcome") : null;
            if (goodbye is { } left) await GoodbyeAsync(meeting.MeetingId, left.Slot, left.Key).ConfigureAwait(false);
            else await CancelledAsync(meeting).ConfigureAwait(false);          // no PC has used the code yet
            return new PairingOutcome.Refused("Adding the other PC was cancelled.");
        }
    }

    /// <summary>Marks a code nobody has used yet as cancelled (plan 0.10): <c>{"type":"cancelled"}</c> in the meeting's
    /// <c>welcome</c> slot, sealed like a welcome, but under a key from the code, as no joining PC's is known.</summary>
    private async Task CancelledAsync(CodeMeeting meeting)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = HouseholdCrypto.Seal(CancelledKey(meeting.CodeKey), LanMessages.Write(new LanMessage { Type = "cancelled" }), Encoding.ASCII.GetBytes("welcome"));
        try
        {
            await relay.PutSlotAsync(meeting.MeetingId, "welcome", body, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The key a cancelled code's marker is sealed under, from the code alone.</summary>
    private static byte[] CancelledKey(byte[] codeKey) => HouseholdCrypto.Hkdf(codeKey, [], Side + "cancelled");

    /// <summary>True when the welcome slot holds the adding PC's goodbye: to this joining PC, or, for a code stopped before any
    /// PC used it, to whoever has the code.</summary>
    private static bool Stopped(byte[] slot, byte[] toJoiner, byte[] codeKey) =>
        Open(toJoiner, slot, "welcome") is { Type: "cancel" } || Open(CancelledKey(codeKey), slot, "welcome") is { Type: "cancelled" };

    /// <summary>A cancel that came before the joining PC's hello was read: it may be in its slot already, its user asked, so it
    /// is looked for once, to say goodbye to; the key sealing to it, or null when there is none.</summary>
    private async Task<byte[]?> JoinerNowAsync(CodeMeeting meeting)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var got = await relay.GetSlotAsync(meeting.MeetingId, "joiner", limit.Token).ConfigureAwait(false);
            return got.Ok && got.Value is { } slot && Checked(slot, meeting.CodeKey, "joiner") is { } keys
                ? SessionKeys(meeting.Eph, keys.Eph, meeting.EphPublic, keys.Eph).ToJoiner
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The joining side, given the code as the user typed it. Cancelled, it tells the adding PC so in the next slot
    /// it would have written, as far as it has got.</summary>
    /// <param name="inHousehold">True when this PC is in a household that joining leaves: the user is told so.</param>
    /// <param name="enter">Takes this PC into the household in the welcome, once the adding PC has said it recorded the joining;
    /// false when this PC may no longer, as when it entered another household meanwhile.</param>
    /// <param name="joining">Heard just before this PC says it is joining: from then on, the pairing only finishes (plan 0.10).</param>
    public async Task<PairingOutcome> JoinAsync(
        string typed, PairingIdentity me, IPromptBroker broker, bool inHousehold, Func<Welcome, MemberInfo, Task<bool>> enter, CancellationToken cancel,
        Action? joining = null)
    {
        if (PairingCode.Normalize(typed) is not { } normalized)
        {
            return new PairingOutcome.Failed("That isn't a code. A code has 16 letters and digits, like K7QM-2XHD-9PW4-R8TA.");
        }
        var meetingId = PairingCode.MeetingId(normalized);
        var codeKey = PairingCode.Key(normalized);
        (byte[] Key, string Slot)? goodbye = null;
        try
        {
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
            goodbye = (toAdder, "answer");
            var before = await relay.GetSlotAsync(meetingId, "welcome", cancel).ConfigureAwait(false);   // plan 0.10: a code stopped already asks nobody
            if (before.Ok && before.Value is { Length: > 0 } already && Stopped(already, toJoiner, codeKey))
            {
                return new PairingOutcome.Refused("The other PC stopped the pairing, so nothing was changed.");
            }

            // While its user is asked, the meeting is watched: the adding PC stopping withdraws the question (plan 0.9).
            using var question = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var watch = WatchForGoodbyeAsync(meetingId, toJoiner, codeKey, deadline, question, watching.Token);
            bool accept;
            try
            {
                accept = await broker.AskToJoinAsync(new JoinQuestion(null, null, inHousehold), question.Token).ConfigureAwait(false);
            }
            finally
            {
                await watching.CancelAsync().ConfigureAwait(false);
            }
            if (await watch.ConfigureAwait(false)) return new PairingOutcome.Refused("The other PC stopped the pairing, so nothing was changed.");
            cancel.ThrowIfCancellationRequested();                                // the question was withdrawn: said below
            var answer = HouseholdCrypto.Seal(toAdder, LanMessages.Write(new LanMessage
            {
                Type = "answer", Accept = accept, Name = me.Name, Kind = Wire.Kind(me.Kind), Instance = me.Instance,
            }), Encoding.ASCII.GetBytes("answer"));
            put = await relay.PutSlotAsync(meetingId, "answer", answer, cancel).ConfigureAwait(false);
            if (!accept) return new PairingOutcome.Refused("This PC didn't join the other PC's household.");
            if (!put.Ok) return new PairingOutcome.Failed($"Couldn't give the other PC the answer: {put.Problem}.");
            goodbye = (toAdder, "joined");

            var sealedWelcome = await PollAsync(meetingId, "welcome", deadline, cancel).ConfigureAwait(false);
            if (sealedWelcome is null) return new PairingOutcome.Failed("The other PC didn't finish adding this PC in time, so nothing was changed.");
            var keysOnly = new MemberInfo(adder.Id, "", ChassisKind.Desktop, adder.Sign, adder.Dh);
            if (Stopped(sealedWelcome, toJoiner, codeKey)) return new PairingOutcome.Refused("The other PC stopped the pairing, so nothing was changed.");
            var message = Open(toJoiner, sealedWelcome, "welcome");
            if (message is not { Type: "welcome" } || PairingSession.ReadWelcome(message, keysOnly) is not { } welcome)
            {
                return new PairingOutcome.Failed("The other PC sent a household that wasn't a good one, so nothing was changed.");
            }
            var from = welcome.Members.First(member => member.Id == adder.Id);
            var joined = HouseholdCrypto.Seal(toAdder, Wire.SignJoin(me.Keys, welcome.HouseholdId), Encoding.ASCII.GetBytes("joined"));
            cancel.ThrowIfCancellationRequested();
            joining?.Invoke();
            goodbye = null;                                                     // joining: no cancel now
            if (!await PutUntilAsync(meetingId, "joined", joined, deadline, CancellationToken.None).ConfigureAwait(false))   // plan 0.10: until it lands
            {
                return new PairingOutcome.Failed($"Couldn't tell {from.Name} this PC is joining before the code ran out. Nothing was changed.");
            }
            var sealedWelcomed = await PollAsync(meetingId, "welcomed", deadline, CancellationToken.None).ConfigureAwait(false);   // joined: no cancel now
            var said = sealedWelcomed is null ? null : Open(toJoiner, sealedWelcomed, "welcomed");
            if (said is { Type: "cancel" }) return new PairingOutcome.Refused($"{from.Name} stopped the pairing, so nothing was changed.");
            if (said is not { Type: "welcomed" }) return new PairingOutcome.Failed($"{from.Name} didn't finish adding this PC in time, so nothing was changed.");
            if (!await enter(welcome, from).ConfigureAwait(false))
            {
                return new PairingOutcome.Failed($"This PC's household changed while it was joining {from.Name}'s, so it didn't join.");
            }
            return new PairingOutcome.Joined(from, $"This PC joined {from.Name}'s household.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (goodbye is { } left) await GoodbyeAsync(meetingId, left.Slot, left.Key).ConfigureAwait(false);
            return new PairingOutcome.Refused("This PC stopped the pairing, so nothing was changed.");
        }
    }

    /// <summary>Watches the welcome slot while the joining PC's user is asked: a goodbye there from the adding PC withdraws the
    /// question.</summary>
    /// <returns>True when the adding PC stopped.</returns>
    private async Task<bool> WatchForGoodbyeAsync(string meetingId, byte[] toJoiner, byte[] codeKey, DateTimeOffset deadline, CancellationTokenSource question,
        CancellationToken watching)
    {
        try
        {
            if (await PollAsync(meetingId, "welcome", deadline, watching).ConfigureAwait(false) is not { } slot || !Stopped(slot, toJoiner, codeKey))
            {
                return false;
            }
            await question.CancelAsync().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;                                                     // answered first
        }
    }

    /// <summary>Tells the other PC this one stopped: <c>{"type":"cancel"}</c>, sealed, in the slot it waits on next, as far as
    /// the server can be reached in a few seconds.</summary>
    private async Task GoodbyeAsync(string meetingId, string slot, byte[] key)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sealedBytes = HouseholdCrypto.Seal(key, LanMessages.Write(new LanMessage { Type = "cancel" }), Encoding.ASCII.GetBytes(slot));
        try
        {
            await relay.PutSlotAsync(meetingId, slot, sealedBytes, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
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
