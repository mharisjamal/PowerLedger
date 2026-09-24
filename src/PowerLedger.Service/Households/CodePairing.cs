using System.Security.Cryptography;
using System.Text;
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
/// makes a code of 80 random bits and puts its hello in the meeting's <c>adder</c> slot; the joining PC, given the code,
/// reads it and puts its own in <c>joiner</c>. Each hello carries a MAC under a key made from the code, so the server, which
/// only sees the meeting ID made from the code, can't put in keys of its own. Both then agree a secret from their ephemeral
/// keys; the joining PC's user is asked, without a comparison code since the code vouches for the adder, and the answer
/// and the welcome go through the <c>answer</c> and <c>welcome</c> slots, sealed. A meeting lasts 10 minutes.
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
        var hello = Hello(meeting.EphPublic, me, meeting.CodeKey, "adder");
        var put = await relay.PutSlotAsync(meeting.MeetingId, "adder", LanMessages.Write(hello), cancel).ConfigureAwait(false);
        if (put.Ok) return meeting;
        meeting.Dispose();
        return null;
    }

    /// <summary>The adding side from there: waits for a joining PC's hello and checks its MAC, then its answer, then sends
    /// the welcome, all within the meeting's 10 minutes.</summary>
    public async Task<PairingOutcome> AddAsync(CodeMeeting meeting, PairingIdentity me, Func<MemberInfo, Welcome> welcomeFor, CancellationToken cancel)
    {
        var deadline = meeting.Opened + Lifetime;
        var slot = await PollAsync(meeting.MeetingId, "joiner", deadline, cancel).ConfigureAwait(false);
        if (slot is null) return new PairingOutcome.Failed("The code ran out before another PC used it.");
        if (Checked(slot, meeting.CodeKey, "joiner") is not { } joiner)
        {
            return new PairingOutcome.Failed("A PC tried the code, but its keys didn't match it, so it wasn't added.");
        }
        if (joiner.From.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That is this PC.");
        var (toJoiner, toAdder) = Keys(meeting.Eph, joiner.Eph, meeting.EphPublic, joiner.Eph);

        var sealedAnswer = await PollAsync(meeting.MeetingId, "answer", deadline, cancel).ConfigureAwait(false);
        if (sealedAnswer is null) return new PairingOutcome.Failed($"{joiner.From.Name} didn't answer before the code ran out.");
        var answer = Open(toAdder, sealedAnswer, "answer");
        if (answer is not { Type: "answer" }) return new PairingOutcome.Failed($"{joiner.From.Name}'s answer didn't open, so it wasn't added.");
        if (answer.Accept != true) return new PairingOutcome.Refused($"{joiner.From.Name} didn't join.");

        var welcome = welcomeFor(joiner.From);
        var sealedWelcome = HouseholdCrypto.Seal(toJoiner, LanMessages.Write(PairingSession.WelcomeMessage(welcome)), Encoding.ASCII.GetBytes("welcome"));
        var put = await relay.PutSlotAsync(meeting.MeetingId, "welcome", sealedWelcome, cancel).ConfigureAwait(false);
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't give {joiner.From.Name} the household: {put.Problem}.");
        return new PairingOutcome.Joined(joiner.From, $"{joiner.From.Name} joined your household.");
    }

    /// <summary>The joining side, given the code as the user typed it.</summary>
    /// <param name="inHousehold">True when this PC is in a household that joining leaves: the user is told so.</param>
    /// <param name="enter">Takes this PC into the household in the welcome.</param>
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
        if (adder.From.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That code was made on this PC. Type it on the other one.");

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var put = await relay.PutSlotAsync(meetingId, "joiner", LanMessages.Write(Hello(ephPublic, me, codeKey, "joiner")), cancel).ConfigureAwait(false);
        if (put.Status == 409) return new PairingOutcome.Failed("Another PC has already used that code.");
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't reach the server: {put.Problem}.");
        var (toJoiner, toAdder) = Keys(eph, adder.Eph, adder.Eph, ephPublic);
        var deadline = clock.GetUtcNow() + Lifetime;

        var accept = await broker.AskToJoinAsync(new JoinQuestion(adder.From.Name, null, inHousehold), cancel).ConfigureAwait(false);
        var answer = HouseholdCrypto.Seal(toAdder, LanMessages.Write(new LanMessage { Type = "answer", Accept = accept }), Encoding.ASCII.GetBytes("answer"));
        put = await relay.PutSlotAsync(meetingId, "answer", answer, cancel).ConfigureAwait(false);
        if (!accept) return new PairingOutcome.Refused($"This PC didn't join {adder.From.Name}'s household.");
        if (!put.Ok) return new PairingOutcome.Failed($"Couldn't give {adder.From.Name} the answer: {put.Problem}.");

        var sealedWelcome = await PollAsync(meetingId, "welcome", deadline, cancel).ConfigureAwait(false);
        if (sealedWelcome is null) return new PairingOutcome.Failed($"{adder.From.Name} didn't finish adding this PC in time, so nothing was changed.");
        if (Open(toJoiner, sealedWelcome, "welcome") is not { Type: "welcome" } message || PairingSession.ReadWelcome(message, adder.From) is not { } welcome)
        {
            return new PairingOutcome.Failed($"{adder.From.Name} sent a household that wasn't a good one, so nothing was changed.");
        }
        await enter(welcome, adder.From).ConfigureAwait(false);
        return new PairingOutcome.Joined(adder.From, $"This PC joined {adder.From.Name}'s household.");
    }

    private static LanMessage Hello(byte[] ephPublic, PairingIdentity me, byte[] codeKey, string side) =>
        LanMessages.Hello(Lan.Hello.Pair, ephPublic, me.Keys, me.Name, me.Kind, me.Instance) with
        {
            Mac = Wire.Encode(PairingCode.Mac(codeKey, side, ephPublic, me.Keys.SignPublic, me.Keys.DhPublic)),
        };

    /// <summary>The hello in a slot, when its MAC under the code's key is right for <paramref name="side"/>.</summary>
    private static Hello? Checked(byte[] slot, byte[] codeKey, string side)
    {
        var message = LanMessages.Read(slot);
        if (Lan.Hello.Of(message) is not { Purpose: Lan.Hello.Pair } hello || Wire.Decode(message!.Mac) is not { } mac) return null;
        var expected = PairingCode.Mac(codeKey, side, hello.Eph, hello.From.Sign, hello.From.Dh);
        return CryptographicOperations.FixedTimeEquals(mac, expected) ? hello : null;
    }

    /// <summary>The keys each way, from the ephemeral keys' shared secret, the adder's key first.</summary>
    private static (byte[] ToJoiner, byte[] ToAdder) Keys(ECDiffieHellman mine, byte[] theirs, byte[] ephAdder, byte[] ephJoiner)
    {
        var shared = HouseholdCrypto.Agree(mine, theirs);
        byte[] salt = [.. ephAdder, .. ephJoiner];
        return (HouseholdCrypto.Hkdf(shared, salt, Side + "a2j"), HouseholdCrypto.Hkdf(shared, salt, Side + "j2a"));
    }

    private static LanMessage? Open(byte[] key, byte[] sealedBytes, string slot)
    {
        try
        {
            return LanMessages.Read(HouseholdCrypto.Open(key, sealedBytes, Encoding.ASCII.GetBytes(slot)));
        }
        catch (CryptographicException)
        {
            return null;
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
