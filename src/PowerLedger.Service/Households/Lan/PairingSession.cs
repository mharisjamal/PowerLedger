using System.Security.Cryptography;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;

namespace PowerLedger.Service.Households.Lan;

/// <summary>This PC as it introduces itself when pairing.</summary>
internal sealed record PairingIdentity(DeviceKeys Keys, string Name, ChassisKind Kind, string Instance);

/// <summary>What the adding PC gives the joining one (households design §3): the household, its key and epoch, and its members.</summary>
internal sealed record Welcome(string HouseholdId, int Epoch, byte[] Key, IReadOnlyList<MemberInfo> Members);

/// <summary>What the user at the joining PC is asked.</summary>
/// <param name="ComparisonCode">"482 913" on the network; null for a pairing by code, which the code vouches for.</param>
/// <param name="LeavesHousehold">True when this PC is in a household that joining leaves.</param>
internal sealed record JoinQuestion(string FromName, string? ComparisonCode, bool LeavesHousehold);

/// <summary>Asks the user at the screen (households design §3, §9). Cancelling a question's token withdraws it: its prompt
/// closes, and the answer is no.</summary>
internal interface IPromptBroker
{
    /// <summary>True when the user pressed Join; false when they pressed Don't join, didn't answer in two minutes, nobody is at
    /// the screen to ask, or the question was withdrawn.</summary>
    Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel);

    /// <summary>The adding PC's own check (plan 0.8): "Does Laptop-2 show 482 913?". True when its user pressed Codes match;
    /// false for Cancel, no answer in two minutes, nobody at the screen, or the question withdrawn.</summary>
    Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel);
}

/// <summary>How a pairing ended, with words the App can show.</summary>
internal abstract record PairingOutcome(string Text)
{
    /// <summary>The other PC joined, or this one joined the other's household. For the adding side, <paramref name="Proof"/> is
    /// the joining PC's signature over its join, which the server wants to add it.</summary>
    public sealed record Joined(MemberInfo Other, string Text, byte[]? Proof = null) : PairingOutcome(Text);

    /// <summary>A user said no or didn't answer, on either side, or cancelled.</summary>
    public sealed record Refused(string Text) : PairingOutcome(Text);

    /// <summary>The connection or the other PC went wrong.</summary>
    public sealed record Failed(string Text) : PairingOutcome(Text);
}

/// <summary>How long each step may take: a reply in the exchange, and an answer that waits on a user.</summary>
internal sealed record PairingTimeouts(TimeSpan Step, TimeSpan Answer)
{
    /// <summary>How long a user has to answer a prompt (households design §3).</summary>
    public static readonly TimeSpan Prompt = TimeSpan.FromMinutes(2);

    public static PairingTimeouts Default { get; } = new(TimeSpan.FromSeconds(30), Prompt + TimeSpan.FromSeconds(30));
}

/// <summary>
/// Pairing on the same network (households design §3, plan 0.6 and 0.8), over one connection. Each side sends a hello with a
/// fresh ephemeral key and its own keys; both agree a shared secret, from which, with the two hellos as they went over the
/// wire, come the keys for the rest of the exchange and the six-digit comparison code both screens show. A PC in the
/// middle that changes anything in either hello can't make the two codes match, except by a one-in-a-million chance. Both
/// users check the code: the joining PC's presses Join, and the adding PC's presses Codes match on "Does Laptop-2 show
/// 482 913?". The household's key goes in the welcome only once both have, whichever answers first; a no or a cancel on
/// either side sends <c>{"type":"cancel"}</c> or a no, and the other side's question closes, as it does when the
/// connection goes.
/// </summary>
internal static class PairingSession
{
    /// <summary>The adding side, which connected.</summary>
    /// <param name="expectedInstance">The instance name the PC chosen was announced under; a hello under another is refused.</param>
    /// <param name="broker">Asks this PC's user whether the other PC shows the same code.</param>
    /// <param name="welcomeFor">Makes the welcome for the joining PC once both users said yes, making the household if there
    /// is none yet.</param>
    public static async Task<PairingOutcome> AddAsync(
        IFrameChannel channel, PairingIdentity me, string? expectedInstance, IPromptBroker broker,
        Func<MemberInfo, Task<Welcome>> welcomeFor, PairingTimeouts timeouts, CancellationToken cancel)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var talk = new LanConversation(channel, timeouts.Step);
        using var question = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<bool>? confirming = null;
        Task<LanMessage>? pending = null;
        string name = "The other PC";
        try
        {
            var myHello = LanMessages.Write(LanMessages.Hello(Hello.Pair, ephPublic, me.Keys, me.Name, me.Kind, me.Instance));
            await talk.SendAsync(myHello, cancel).ConfigureAwait(false);
            var (theirMessage, theirHello) = await talk.ReceiveWithBytesAsync("hello", cancel).ConfigureAwait(false);
            var hello = Hello.Of(theirMessage);
            if (hello is not { Purpose: Hello.Pair }) return new PairingOutcome.Failed("The other PC didn't answer as a PowerLedger PC should.");
            name = hello.From.Name;
            if (expectedInstance is not null && hello.Instance != expectedInstance)
            {
                return new PairingOutcome.Failed($"A different PC answered in place of {name}. Look for it again and try once more.");
            }
            if (hello.From.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That is this PC.");

            var shared = HouseholdCrypto.Agree(eph, hello.Eph);
            var transcript = HouseholdCrypto.Transcript(myHello, theirHello);
            using var cipher = FrameCipher.For(adder: true, shared, transcript);
            talk.Secure(cipher);

            // Both users answer at once: this PC's to the code, the joining PC's with Join; the key waits for both.
            confirming = broker.ConfirmCodeAsync(name, HouseholdCrypto.ComparisonCode(shared, transcript), question.Token);
            pending = talk.ReceiveAsync(null, cancel, timeouts.Answer);
            if (await Task.WhenAny(confirming, pending).ConfigureAwait(false) == confirming && !await confirming.ConfigureAwait(false))
            {
                await CancelAsync(talk, cancel).ConfigureAwait(false);
                return new PairingOutcome.Refused($"Adding {name} was cancelled.");
            }
            var answer = await pending.ConfigureAwait(false);
            if (answer is not { Type: "answer", Accept: true })
            {
                return new PairingOutcome.Refused($"{name} didn't join.");
            }

            pending = talk.ReceiveAsync(null, cancel, timeouts.Answer);       // a cancel from the other side, or its "joined"
            if (await Task.WhenAny(confirming, pending).ConfigureAwait(false) == pending)
            {
                question.Cancel();
                return (await pending.ConfigureAwait(false)).Type == "cancel"
                    ? new PairingOutcome.Refused($"{name} stopped the pairing.")
                    : new PairingOutcome.Failed($"The connection to {name} went wrong, so nothing was changed.");
            }
            if (!await confirming.ConfigureAwait(false))
            {
                await CancelAsync(talk, cancel).ConfigureAwait(false);
                return new PairingOutcome.Refused($"Adding {name} was cancelled.");
            }

            var welcome = await welcomeFor(hello.From).ConfigureAwait(false);
            await talk.SendAsync(WelcomeMessage(welcome), cancel).ConfigureAwait(false);
            var joined = await pending.ConfigureAwait(false);
            pending = null;
            if (joined.Type != "joined") throw new LanException(LanProblem.Broken);
            var proof = Wire.Decode(joined.Proof);
            if (!Wire.IsJoinProof(hello.From, welcome.HouseholdId, proof))
            {
                return new PairingOutcome.Failed($"{name} didn't sign its joining, so it wasn't added.");
            }
            return new PairingOutcome.Joined(hello.From, $"{name} joined your household.", proof);
        }
        catch (LanException error)
        {
            return new PairingOutcome.Failed(error.Problem switch
            {
                LanProblem.Timeout => $"{name} didn't answer in time.",
                LanProblem.Closed => $"{name} closed the connection.",
                _ => $"The connection to {name} went wrong, so nothing was changed.",
            });
        }
        finally
        {
            await CloseAsync(question, confirming, pending).ConfigureAwait(false);
        }
    }

    /// <summary>The joining side, which was connected to and has read the adder's hello.</summary>
    /// <param name="adderHello">The adder's hello as it came, which the transcript takes.</param>
    /// <param name="inHousehold">True when this PC is in a household, which joining leaves: the user is told so.</param>
    /// <param name="enter">Takes this PC into the household in the welcome, from the adding PC.</param>
    public static async Task<PairingOutcome> JoinAsync(
        IFrameChannel channel, byte[] adderHello, PairingIdentity me, IPromptBroker broker, bool inHousehold,
        Func<Welcome, MemberInfo, Task> enter, PairingTimeouts timeouts, CancellationToken cancel)
    {
        if (Hello.Of(LanMessages.Read(adderHello)) is not { Purpose: Hello.Pair } hello) return new PairingOutcome.Failed("The other PC's hello wasn't a good one.");
        if (hello.From.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That is this PC.");
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var talk = new LanConversation(channel, timeouts.Step);
        var name = hello.From.Name;
        using var question = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<bool>? asking = null;
        Task<LanMessage>? pending = null;
        try
        {
            var myHello = LanMessages.Write(LanMessages.Hello(Hello.Pair, ephPublic, me.Keys, me.Name, me.Kind, me.Instance));
            await talk.SendAsync(myHello, cancel).ConfigureAwait(false);
            var shared = HouseholdCrypto.Agree(eph, hello.Eph);
            var transcript = HouseholdCrypto.Transcript(adderHello, myHello);
            using var cipher = FrameCipher.For(adder: false, shared, transcript);
            talk.Secure(cipher);

            // The user's answer, while the connection is watched: a cancel from the adder, or the connection going, closes the question.
            asking = broker.AskToJoinAsync(new JoinQuestion(name, HouseholdCrypto.ComparisonCode(shared, transcript), inHousehold), question.Token);
            pending = talk.ReceiveAsync(null, cancel, timeouts.Answer);
            if (await Task.WhenAny(asking, pending).ConfigureAwait(false) == pending)
            {
                question.Cancel();
                return (await pending.ConfigureAwait(false)).Type == "cancel"
                    ? new PairingOutcome.Refused($"{name} stopped the pairing, so nothing was changed.")
                    : new PairingOutcome.Failed($"The connection to {name} went wrong, so nothing was changed.");
            }
            var accept = await asking.ConfigureAwait(false);
            await talk.SendAsync(new LanMessage { Type = "answer", Accept = accept }, cancel).ConfigureAwait(false);
            if (!accept) return new PairingOutcome.Refused($"This PC didn't join {name}'s household.");

            var message = await pending.ConfigureAwait(false);
            pending = null;
            if (message.Type == "cancel") return new PairingOutcome.Refused($"{name} stopped the pairing, so nothing was changed.");
            if (message.Type != "welcome" || ReadWelcome(message, hello.From) is not { } welcome)
            {
                return new PairingOutcome.Failed($"{name} sent a household that wasn't a good one, so nothing was changed.");
            }
            await enter(welcome, hello.From).ConfigureAwait(false);
            await talk.SendAsync(new LanMessage { Type = "joined", Proof = Wire.Encode(Wire.SignJoin(me.Keys, welcome.HouseholdId)) }, cancel)
                .ConfigureAwait(false);
            return new PairingOutcome.Joined(hello.From, $"This PC joined {name}'s household.");
        }
        catch (LanException error)
        {
            return new PairingOutcome.Failed(error.Problem == LanProblem.Timeout
                ? $"{name} didn't answer in time, so nothing was changed."
                : $"The connection to {name} went wrong, so nothing was changed.");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return new PairingOutcome.Failed($"The connection to {name} went wrong.");
        }
        finally
        {
            await CloseAsync(question, asking, pending).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the other side this one stopped, as far as the connection lets it.</summary>
    private static async Task CancelAsync(LanConversation talk, CancellationToken cancel)
    {
        try
        {
            await talk.SendAsync(new LanMessage { Type = "cancel" }, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or LanException)
        {
        }
    }

    /// <summary>Withdraws a question still open, and lets go of a read still waiting, whose connection the caller closes.</summary>
    private static async Task CloseAsync(CancellationTokenSource question, Task<bool>? asking, Task<LanMessage>? pending)
    {
        await question.CancelAsync().ConfigureAwait(false);
        if (asking is not null)
        {
            try
            {
                await asking.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _ = pending?.ContinueWith(read => read.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public static LanMessage WelcomeMessage(Welcome welcome) => new()
    {
        Type = "welcome",
        Household = welcome.HouseholdId,
        Epoch = welcome.Epoch,
        Key = Wire.Encode(welcome.Key),
        Members = [.. welcome.Members.Select(Wire.Member)],
    };

    /// <summary>The welcome in <paramref name="message"/>, checked: a household ID, an epoch, a 32-byte key and at most 16
    /// members, each with the ID its key makes, the adder among them under the keys it paired with. Null otherwise.</summary>
    public static Welcome? ReadWelcome(LanMessage message, MemberInfo adder)
    {
        if (!Wire.IsHouseholdId(message.Household) || message.Epoch is not > 0 || Wire.Decode(message.Key) is not { Length: HouseholdCrypto.KeyLength } key)
        {
            return null;
        }
        if (message.Members is not { Count: > 0 and <= Wire.MaxMembers } sent) return null;
        var members = sent.Select(Wire.Member).ToList();
        if (members.Any(member => member is null)) return null;
        if (!members.Any(member => member!.Id == adder.Id && member.Sign.AsSpan().SequenceEqual(adder.Sign) && member.Dh.AsSpan().SequenceEqual(adder.Dh)))
        {
            return null;
        }
        return new Welcome(message.Household!, message.Epoch.Value, key, [.. members!]);
    }
}

/// <summary>
/// One pairing at a time, and a pause after too many refusals (households design §3): 5 refused pairings in 10 minutes stop
/// pairing for 10 minutes, so nobody can keep asking this PC's user, or keep trying for a matching code.
/// </summary>
internal sealed class PairingGate(TimeProvider clock)
{
    public const int MaxRefusals = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(10);

    internal const string Busy = "This PC is already pairing with another. Try again when that's done.";
    internal const string Paused = "Pairing is paused for 10 minutes after several refusals.";

    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _refusals = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private bool _busy;

    /// <summary>Starts a pairing; disposing the result ends it.</summary>
    /// <returns>Null, with the reason, while another pairing runs or pairing is paused.</returns>
    public IDisposable? TryEnter(out string? refusal)
    {
        lock (_gate)
        {
            if (clock.GetUtcNow() < _pausedUntil)
            {
                refusal = Paused;
                return null;
            }
            if (_busy)
            {
                refusal = Busy;
                return null;
            }
            _busy = true;
            refusal = null;
            return new Entered(this);
        }
    }

    /// <summary>Counts a refused pairing, pausing pairing once there have been too many.</summary>
    public void Refused()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            _refusals.Enqueue(now);
            while (_refusals.Count > 0 && now - _refusals.Peek() > Window) _refusals.Dequeue();
            if (_refusals.Count < MaxRefusals) return;
            _pausedUntil = now + Pause;
            _refusals.Clear();
        }
    }

    private void Leave()
    {
        lock (_gate) _busy = false;
    }

    private sealed class Entered(PairingGate gate) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) gate.Leave();
        }
    }
}
