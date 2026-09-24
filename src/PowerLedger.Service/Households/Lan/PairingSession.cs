using System.Net;
using System.Security.Cryptography;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;

namespace PowerLedger.Service.Households.Lan;

/// <summary>This PC as it introduces itself when pairing.</summary>
internal sealed record PairingIdentity(DeviceKeys Keys, string Name, ChassisKind Kind, string Instance);

/// <summary>What the adding PC gives the joining one (households design §3, plan 0.9): the household, its key and epoch, and the
/// members it knows with their epochs, the removed ones among them.</summary>
/// <param name="Members">The current members, with their keys.</param>
/// <param name="Entries">Every member the adding PC knows, current or removed, as the welcome carries them.</param>
internal sealed record Welcome(string HouseholdId, int Epoch, byte[] Key, IReadOnlyList<MemberInfo> Members, IReadOnlyList<WireMember>? Entries = null)
{
    /// <summary>Every member the welcome carries: its entries, or its current members when made without them.</summary>
    public IReadOnlyList<WireMember> AllEntries => Entries ?? [.. Members.Select(Wire.Member)];
}

/// <summary>What the user at the joining PC is asked.</summary>
/// <param name="FromName">The adding PC's name; null for a pairing by code, whose meeting carries no names.</param>
/// <param name="ComparisonCode">"482 913" on the network; null for a pairing by code, which the code vouches for.</param>
/// <param name="LeavesHousehold">True when this PC is in a household that joining leaves.</param>
internal sealed record JoinQuestion(string? FromName, string? ComparisonCode, bool LeavesHousehold);

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
/// middle that changes anything in either hello can't make the two codes match, except by a one-in-a-million chance: the
/// adder commits to a nonce in its hello and reveals it only once the joiner's hello has come (plan 0.9), and the code is
/// over the nonce too, so the side that sends its hello second can't try hellos until the code comes out as it wants, and
/// the adder can't pick its nonce after seeing the joiner's hello. The joiner asks its user only after a good reveal. Both
/// users check the code: the joining PC's presses Join, and the adding PC's presses Codes match on "Does Laptop-2 show
/// 482 913?". The household's key goes in the welcome only once both have, whichever answers first; a no or a cancel on
/// either side sends <c>{"type":"cancel"}</c> or a no, and the other side's question closes, as it does when the
/// connection goes. The joining PC keeps the welcome aside, signs its joining, and enters the household only once the
/// adding PC, having recorded it, answers <c>{"type":"welcomed"}</c>; without that, nothing changes on it.
/// </summary>
internal static class PairingSession
{
    /// <summary>The adding side, which connected.</summary>
    /// <param name="expectedInstance">The instance name the PC chosen was announced under; a hello under another is refused.</param>
    /// <param name="broker">Asks this PC's user whether the other PC shows the same code.</param>
    /// <param name="welcomeFor">Makes the welcome for the joining PC once both users said yes, making the household if there
    /// is none yet; null when the joining PC can't be added yet, as a PC removed at this PC's epoch can't.</param>
    /// <param name="record">Records the joining PC as a member, with its proof for the server, before it is told it is in;
    /// false when it couldn't be.</param>
    public static async Task<PairingOutcome> AddAsync(
        IFrameChannel channel, PairingIdentity me, string? expectedInstance, IPromptBroker broker,
        Func<MemberInfo, Task<Welcome?>> welcomeFor, Func<MemberInfo, byte[], Task<bool>> record, PairingTimeouts timeouts, CancellationToken cancel)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var talk = new LanConversation(channel, timeouts.Step);
        using var question = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<bool>? confirming = null;
        Task<LanMessage>? pending = null;
        FrameCipher? cipher = null;                                            // kept to say goodbye after a cancel
        string name = "The other PC";
        var nonce = HouseholdCrypto.NewNonce();                                // bound in the hello before the joiner's comes
        try
        {
            var myHello = LanMessages.Write(LanMessages.Hello(Hello.Pair, ephPublic, me.Keys, me.Name, me.Kind, me.Instance) with
            {
                Commit = Wire.Encode(HouseholdCrypto.Commitment(nonce)),
            });
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
            cipher = FrameCipher.For(adder: true, shared, transcript);
            talk.Secure(cipher);
            await talk.SendAsync(new LanMessage { Type = "reveal", Nonce = Wire.Encode(nonce) }, cancel).ConfigureAwait(false);

            // Both users answer at once: this PC's to the code, the joining PC's with Join; the key waits for both.
            confirming = broker.ConfirmCodeAsync(name, HouseholdCrypto.ComparisonCode(shared, transcript, nonce), question.Token);
            pending = talk.ReceiveAsync(null, CancellationToken.None, timeouts.Answer);
            if (await Task.WhenAny(confirming, pending).ConfigureAwait(false) == confirming && !await confirming.ConfigureAwait(false))
            {
                await CancelAsync(talk).ConfigureAwait(false);
                return new PairingOutcome.Refused($"Adding {name} was cancelled.");
            }
            var answer = await Until(pending, cancel).ConfigureAwait(false);
            if (answer is not { Type: "answer", Accept: true })
            {
                return new PairingOutcome.Refused(answer.Type == "cancel" ? $"{name} stopped the pairing." : $"{name} didn't join.");
            }

            pending = talk.ReceiveAsync(null, CancellationToken.None, timeouts.Answer);   // a cancel from the other side, or its "joined"
            if (await Task.WhenAny(confirming, pending).ConfigureAwait(false) == pending)
            {
                question.Cancel();
                return (await pending.ConfigureAwait(false)).Type == "cancel"
                    ? new PairingOutcome.Refused($"{name} stopped the pairing.")
                    : new PairingOutcome.Failed($"The connection to {name} went wrong, so nothing was changed.");
            }
            if (!await confirming.ConfigureAwait(false))
            {
                await CancelAsync(talk).ConfigureAwait(false);
                return new PairingOutcome.Refused($"Adding {name} was cancelled.");
            }

            if (await welcomeFor(hello.From).ConfigureAwait(false) is not { } welcome)
            {
                await CancelAsync(talk).ConfigureAwait(false);
                return new PairingOutcome.Failed(NotYet(name));
            }
            await talk.SendAsync(WelcomeMessage(welcome), cancel).ConfigureAwait(false);
            var joined = await Until(pending, cancel).ConfigureAwait(false);
            pending = null;
            if (joined.Type != "joined") throw new LanException(LanProblem.Broken);
            var proof = Wire.Decode(joined.Proof);
            if (!Wire.IsJoinProof(hello.From, welcome.HouseholdId, proof))
            {
                return new PairingOutcome.Failed($"{name} didn't sign its joining, so it wasn't added.");
            }
            if (!await record(hello.From, proof!).ConfigureAwait(false))
            {
                await CancelAsync(talk).ConfigureAwait(false);
                return new PairingOutcome.Failed(NotYet(name));
            }
            try
            {
                await talk.SendAsync(new LanMessage { Type = "welcomed" }, cancel).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                return new PairingOutcome.Failed(
                    $"The connection to {name} went as it joined. If {name} doesn't show your household, remove it here and add it again.");
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
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await CancelAsync(talk).ConfigureAwait(false);
            return new PairingOutcome.Refused($"Adding {name} was cancelled.");
        }
        finally
        {
            await CloseAsync(question, confirming, pending).ConfigureAwait(false);
            cipher?.Dispose();
        }
    }

    /// <summary>The joining side, which was connected to and has read the adder's hello.</summary>
    /// <param name="adderHello">The adder's hello as it came, which the transcript takes.</param>
    /// <param name="inHousehold">True when this PC is in a household, which joining leaves: the user is told so.</param>
    /// <param name="enter">Takes this PC into the household in the welcome, from the adding PC, once the adding PC has said
    /// it recorded the joining.</param>
    /// <param name="refused">Counts a pairing whose question came to nothing: the user said no, or the adding PC stopped or
    /// went while it was open. It is counted before the answer goes, so the next try already meets the count. A pairing that
    /// ends before a good reveal isn't counted here; the caller counts every pairing that comes to nothing.</param>
    public static async Task<PairingOutcome> JoinAsync(
        IFrameChannel channel, byte[] adderHello, PairingIdentity me, IPromptBroker broker, bool inHousehold,
        Func<Welcome, MemberInfo, Task> enter, PairingTimeouts timeouts, CancellationToken cancel, Action? refused = null)
    {
        if (Hello.Of(LanMessages.Read(adderHello)) is not { Purpose: Hello.Pair, Commit: { } commit } hello)
        {
            return new PairingOutcome.Failed("The other PC's hello wasn't a good one.");
        }
        if (hello.From.Id == me.Keys.DeviceId) return new PairingOutcome.Failed("That is this PC.");
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var talk = new LanConversation(channel, timeouts.Step);
        var name = hello.From.Name;
        using var question = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<bool>? asking = null;
        Task<LanMessage>? pending = null;
        FrameCipher? cipher = null;                                            // kept to say goodbye after a cancel
        try
        {
            var myHello = LanMessages.Write(LanMessages.Hello(Hello.Pair, ephPublic, me.Keys, me.Name, me.Kind, me.Instance));
            await talk.SendAsync(myHello, cancel).ConfigureAwait(false);
            var shared = HouseholdCrypto.Agree(eph, hello.Eph);
            var transcript = HouseholdCrypto.Transcript(adderHello, myHello);
            cipher = FrameCipher.For(adder: false, shared, transcript);
            talk.Secure(cipher);
            var reveal = await talk.ReceiveAsync("reveal", cancel).ConfigureAwait(false);
            if (Wire.Decode(reveal.Nonce) is not { Length: 32 } nonce
                || !CryptographicOperations.FixedTimeEquals(HouseholdCrypto.Commitment(nonce), commit))
            {
                return new PairingOutcome.Failed($"{name}'s pairing didn't hold together, so nothing was changed.");
            }

            // The user's answer, while the connection is watched: a cancel from the adder, or the connection going, closes the question.
            var code = HouseholdCrypto.ComparisonCode(shared, transcript, nonce);
            asking = broker.AskToJoinAsync(new JoinQuestion(name, code, inHousehold), question.Token);
            pending = talk.ReceiveAsync(null, CancellationToken.None, timeouts.Answer);
            if (await Task.WhenAny(asking, pending).ConfigureAwait(false) == pending)
            {
                question.Cancel();
                refused?.Invoke();
                return (await pending.ConfigureAwait(false)).Type == "cancel"
                    ? new PairingOutcome.Refused($"{name} stopped the pairing, so nothing was changed.")
                    : new PairingOutcome.Failed($"The connection to {name} went wrong, so nothing was changed.");
            }
            var accept = await asking.ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();                                // the question was withdrawn: said below
            if (!accept) refused?.Invoke();
            await talk.SendAsync(new LanMessage { Type = "answer", Accept = accept }, cancel).ConfigureAwait(false);
            if (!accept) return new PairingOutcome.Refused($"This PC didn't join {name}'s household.");

            var message = await Until(pending, cancel).ConfigureAwait(false);
            pending = null;
            if (message.Type == "cancel") return new PairingOutcome.Refused($"{name} stopped the pairing, so nothing was changed.");
            if (message.Type != "welcome" || ReadWelcome(message, hello.From) is not { } welcome)
            {
                return new PairingOutcome.Failed($"{name} sent a household that wasn't a good one, so nothing was changed.");
            }
            await talk.SendAsync(new LanMessage { Type = "joined", Proof = Wire.Encode(Wire.SignJoin(me.Keys, welcome.HouseholdId)) }, cancel)
                .ConfigureAwait(false);
            await Until(talk.ReceiveAsync("welcomed", CancellationToken.None), cancel).ConfigureAwait(false);
            await enter(welcome, hello.From).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await CancelAsync(talk).ConfigureAwait(false);
            return new PairingOutcome.Refused("This PC stopped the pairing, so nothing was changed.");
        }
        finally
        {
            await CloseAsync(question, asking, pending).ConfigureAwait(false);
            cipher?.Dispose();
        }
    }

    /// <summary>Tells the other side this one stopped, as far as the connection lets it in a few seconds.</summary>
    private static async Task CancelAsync(LanConversation talk)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await talk.SendAsync(new LanMessage { Type = "cancel" }, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or LanException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The read's message, or <see cref="OperationCanceledException"/> once the pairing is cancelled. The read itself isn't
    /// cancelled, only let go of: cancelling a read on a socket can leave the connection unusable, and a cancelled pairing
    /// still says so to the other side before the caller closes the connection.
    /// </summary>
    private static async Task<LanMessage> Until(Task<LanMessage> read, CancellationToken cancel)
    {
        if (!cancel.CanBeCanceled) return await read.ConfigureAwait(false);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancel.Register(() => cancelled.TrySetResult()))
        {
            if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
            {
                _ = read.ContinueWith(done => done.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new OperationCanceledException(cancel);
            }
        }
        return await read.ConfigureAwait(false);
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

    /// <summary>Said when the joining PC can't be added yet: a PC removed at this PC's epoch comes back only once this PC has
    /// made the household a new key, which needs the server (plan 0.9).</summary>
    internal static string NotYet(string name) =>
        $"{name} can't be added until this PC has given the household a new key, which needs the server. Check this PC is online, then try again.";

    /// <summary>The welcome as sent: every member the adding PC knows, with their epochs. With <paramref name="maxBytes"/>, as for
    /// a meeting slot, the members removed earliest are left out until it fits; the current ones always go.</summary>
    public static LanMessage WelcomeMessage(Welcome welcome, int? maxBytes = null)
    {
        var entries = welcome.AllEntries.ToList();
        var message = new LanMessage
        {
            Type = "welcome",
            Household = welcome.HouseholdId,
            Epoch = welcome.Epoch,
            Key = Wire.Encode(welcome.Key),
            Members = entries,
        };
        var removed = entries.Where(entry => !IsCurrent(entry)).OrderBy(entry => entry.RemovedEpoch).ToList();
        while (maxBytes is { } most && removed.Count > 0 && LanMessages.Write(message).Length > most)
        {
            entries.Remove(removed[0]);
            removed.RemoveAt(0);
        }
        return message;
    }

    /// <summary>The welcome in <paramref name="message"/>, checked: a household ID, an epoch, a 32-byte key, and at most 16 current
    /// members, each with the ID its key makes, the adder among them under the keys it paired with, beside at most
    /// <see cref="MemberBook.MaxRemoved"/> removed ones. Null otherwise.</summary>
    public static Welcome? ReadWelcome(LanMessage message, MemberInfo adder)
    {
        if (!Wire.IsHouseholdId(message.Household) || message.Epoch is not > 0 || Wire.Decode(message.Key) is not { Length: HouseholdCrypto.KeyLength } key)
        {
            return null;
        }
        if (message.Members is not { Count: > 0 } sent || sent.Count > Wire.MaxMembers + MemberBook.MaxRemoved) return null;
        if (sent.Any(entry => !Wire.IsDeviceId(entry?.Id))) return null;
        var current = sent.Where(IsCurrent).ToList();
        if (current.Count > Wire.MaxMembers) return null;
        var members = current.Select(Wire.Member).ToList();
        if (members.Any(member => member is null)) return null;
        if (!members.Any(member => member!.Id == adder.Id && member.Sign.AsSpan().SequenceEqual(adder.Sign) && member.Dh.AsSpan().SequenceEqual(adder.Dh)))
        {
            return null;
        }
        return new Welcome(message.Household!, message.Epoch.Value, key, [.. members!], sent);
    }

    private static bool IsCurrent(WireMember entry) => entry.RemovedEpoch is not { } removed || (entry.AddedEpoch ?? 0) > removed;
}

/// <summary>
/// Pairings asked of this PC from the network (plan 0.9). One that comes to nothing, refused, stopped or ended before a good
/// reveal, counts against the address it came from, an IPv6 one by its /64: 3 in 10 minutes and that address goes
/// unanswered for 10 minutes; 20 from all addresses together and every pairing from the network does. A full table keeps
/// its paused addresses, and while it holds nothing else a new address goes unanswered. At most 3 notices in 10 minutes
/// about pairings that went wrong reach the App, so a stranger can't fill the tray. None of it pauses pairing by code.
/// </summary>
internal sealed class StrangerGate(TimeProvider clock, int allAddresses = StrangerGate.AllAddresses, int maxAddresses = StrangerGate.MaxAddresses)
{
    public const int MaxNotices = 3;
    public const int PerAddress = 3;
    public const int AllAddresses = 20;
    public const int MaxAddresses = 256;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Queue<DateTimeOffset> Failures, DateTimeOffset PausedUntil)> _addresses = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _all = new();
    private readonly Queue<DateTimeOffset> _notices = new();
    private DateTimeOffset _allPausedUntil = DateTimeOffset.MinValue;

    /// <summary>False while pairings from <paramref name="from"/> go unanswered: that address, or all of them, paused, or a
    /// new address with the table full of paused ones.</summary>
    public bool Allowed(IPAddress from)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (now < _allPausedUntil) return false;
            if (_addresses.TryGetValue(Key(from), out var seen)) return now >= seen.PausedUntil;
            return _addresses.Count < maxAddresses || _addresses.Values.Any(entry => now >= entry.PausedUntil);
        }
    }

    /// <summary>Counts a pairing from <paramref name="from"/> that came to nothing.</summary>
    public void Failed(IPAddress from)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            Trim(_all, now);
            _all.Enqueue(now);
            if (_all.Count >= allAddresses)
            {
                _allPausedUntil = now + Pause;
                _all.Clear();
            }
            var key = Key(from);
            if (!_addresses.TryGetValue(key, out var seen))
            {
                if (_addresses.Count >= maxAddresses && !Forget(now)) return;     // full of paused addresses: this one isn't answered
                seen = (new Queue<DateTimeOffset>(), DateTimeOffset.MinValue);
            }
            Trim(seen.Failures, now);
            seen.Failures.Enqueue(now);
            if (seen.Failures.Count >= PerAddress) seen = (new Queue<DateTimeOffset>(), now + Pause);
            _addresses[key] = seen;
        }
    }

    /// <summary>An address as it is counted: an IPv4 one whole, an IPv6 one by its /64, which one home is given at once.</summary>
    internal static string Key(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexString(address.GetAddressBytes(), 0, 8) + "/64";
    }

    private static void Trim(Queue<DateTimeOffset> times, DateTimeOffset now)
    {
        while (times.Count > 0 && now - times.Peek() > Window) times.Dequeue();
    }

    /// <summary>True when a notice about a pairing from the network may go to the App now.</summary>
    public bool MayTell()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            while (_notices.Count > 0 && now - _notices.Peek() > PairingGate.Window) _notices.Dequeue();
            if (_notices.Count >= MaxNotices) return false;
            _notices.Enqueue(now);
            return true;
        }
    }

    /// <summary>Makes room in a full table: addresses not paused go, those quiet longest first; a paused one never does.</summary>
    /// <returns>False when every address in the table is paused.</returns>
    private bool Forget(DateTimeOffset now)
    {
        var free = _addresses.Where(pair => now >= pair.Value.PausedUntil)
            .OrderBy(pair => pair.Value.Failures.Count == 0 ? DateTimeOffset.MinValue : pair.Value.Failures.Last())
            .Take(Math.Max(1, maxAddresses / 4)).Select(pair => pair.Key).ToList();
        foreach (var address in free) _addresses.Remove(address);
        return free.Count > 0;
    }
}

/// <summary>
/// One pairing at a time, and a pause after too many refusals (households design §3): 5 refused pairings by code in 10
/// minutes stop pairing for 10 minutes. This PC's own adds that the other PC refuses don't count, and pairings asked from
/// the network count in <see cref="StrangerGate"/> instead, so they never pause pairing by code (plan 0.9).
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
