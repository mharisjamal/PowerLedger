using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Pairing on the same network (households design §3): hellos, the comparison code, the prompt, then the welcome.</summary>
public sealed class PairingSessionTests : IDisposable
{
    private static readonly PairingTimeouts Quick = new(Step: TimeSpan.FromSeconds(5), Answer: TimeSpan.FromSeconds(5));

    private readonly DeviceKeys _adderKeys = DeviceKeys.Create();
    private readonly DeviceKeys _joinerKeys = DeviceKeys.Create();

    private PairingIdentity Adder => new(_adderKeys, "Desktop-7", ChassisKind.Desktop, "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0");

    private PairingIdentity Joiner => new(_joinerKeys, "Laptop-2", ChassisKind.Laptop, "b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1");

    [Fact]
    public async Task Accepting_joins_the_household_and_both_sides_learn_each_other()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var household = HouseholdCrypto.NewKey();
        string? shownOnAdder = null;
        JoinQuestion? asked = null;
        Welcome? entered = null;

        var adding = PairingSession.AddAsync(
            adderEnd, Adder, expectedInstance: Joiner.Instance,
            showCode: (joiner, code) => { shownOnAdder = code; return Task.CompletedTask; },
            welcomeFor: joiner => Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, household, [Member(Adder)])),
            Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new Broker(question => { asked = question; return true; }), inHousehold: false,
            enter: (welcome, adder) => { entered = welcome; return Task.CompletedTask; });

        var added = (await adding).ShouldBeOfType<PairingOutcome.Joined>();
        var joined = (await joining).ShouldBeOfType<PairingOutcome.Joined>();
        Wire.IsJoinProof(added.Other, "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", added.Proof).ShouldBeTrue();

        added.Other.Id.ShouldBe(_joinerKeys.DeviceId);
        added.Other.Name.ShouldBe("Laptop-2");
        added.Other.Kind.ShouldBe(ChassisKind.Laptop);
        added.Other.Dh.ShouldBe(_joinerKeys.DhPublic);
        joined.Other.Id.ShouldBe(_adderKeys.DeviceId);
        asked.ShouldNotBeNull().FromName.ShouldBe("Desktop-7");
        asked.ComparisonCode.ShouldNotBeNull().ShouldMatch("^[0-9]{3} [0-9]{3}$");
        asked.ComparisonCode.ShouldBe(shownOnAdder);
        asked.LeavesHousehold.ShouldBeFalse();
        entered.ShouldNotBeNull().HouseholdId.ShouldBe("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f");
        entered.Key.ShouldBe(household);
        entered.Members.ShouldHaveSingleItem().Sign.ShouldBe(_adderKeys.SignPublic);
    }

    [Fact]
    public async Task Refusing_changes_nothing_on_either_side()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var welcomed = false;
        var entered = false;

        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, _) => Task.CompletedTask,
            joiner => { welcomed = true; return Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey(), [Member(Adder)])); },
            Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new Broker(_ => false), inHousehold: false, enter: (_, _) => { entered = true; return Task.CompletedTask; });

        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Laptop-2 didn't join.");
        (await joining).ShouldBeOfType<PairingOutcome.Refused>();
        welcomed.ShouldBeFalse();
        entered.ShouldBeFalse();
    }

    [Fact]
    public async Task A_prompt_nobody_answers_counts_as_dont_join_and_an_answer_that_never_comes_ends_the_adding()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var clock = new FakeTimeProvider();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, _) => Task.CompletedTask,
            _ => Task.FromException<Welcome>(new InvalidOperationException("never welcomed")), Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new Broker(async (_, cancel) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2), clock, cancel);   // the broker's time-out
            return false;
        }), inHousehold: false, enter: (_, _) => throw new InvalidOperationException("never entered"));

        for (var step = 0; step < 1000 && !joining.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5);
        }
        (await joining).ShouldBeOfType<PairingOutcome.Refused>();
        (await adding).ShouldBeOfType<PairingOutcome.Refused>();

        var (lonelyEnd, silentEnd) = FramePipe.Create();
        var waiting = PairingSession.AddAsync(lonelyEnd, Adder, null, (_, _) => Task.CompletedTask,
            _ => Task.FromException<Welcome>(new InvalidOperationException("never welcomed")), new PairingTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);
        await silentEnd.ReceiveAsync();
        await silentEnd.SendAsync(Hello(Joiner));                          // a hello, then nothing
        (await waiting).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("Laptop-2 didn't answer in time.");
    }

    [Fact]
    public async Task A_joiner_already_in_a_household_is_warned_that_joining_leaves_it()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        JoinQuestion? asked = null;
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey(), [Member(Adder)])), Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new Broker(question => { asked = question; return false; }), inHousehold: true,
            enter: (_, _) => Task.CompletedTask);

        await adding;
        await joining;
        asked.ShouldNotBeNull().LeavesHousehold.ShouldBeTrue();
    }

    [Fact]
    public async Task A_pc_in_the_middle_that_swaps_the_ephemeral_keys_makes_the_codes_differ_and_the_pairing_is_refused()
    {
        var (adderEnd, middleFromAdder) = FramePipe.Create();
        var (middleToJoiner, joinerEnd) = FramePipe.Create();
        string? shownOnAdder = null;
        string? shownOnJoiner = null;
        var entered = false;

        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, code) => { shownOnAdder = code; return Task.CompletedTask; },
            _ => Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey(), [Member(Adder)])), Quick, CancellationToken.None);
        // The user at the joiner compares the two screens, and says no when the codes differ.
        var joining = JoinerSide(joinerEnd, new Broker(question =>
        {
            shownOnJoiner = question.ComparisonCode;
            return question.ComparisonCode == Volatile.Read(ref shownOnAdder);
        }), inHousehold: false, enter: (_, _) => { entered = true; return Task.CompletedTask; });

        // The middle swaps each hello's ephemeral key for its own, then passes the rest along as it is.
        using var middleEph = DeviceKeys.Create();
        await middleToJoiner.SendAsync(SwapEph(await middleFromAdder.ReceiveAsync(), middleEph));
        await middleFromAdder.SendAsync(SwapEph(await middleToJoiner.ReceiveAsync(), middleEph));
        var relaying = Task.WhenAll(Relay(middleFromAdder, middleToJoiner), Relay(middleToJoiner, middleFromAdder));

        var joined = await joining;
        var added = await adding;

        shownOnAdder.ShouldNotBeNull();
        shownOnJoiner.ShouldNotBeNull().ShouldNotBe(shownOnAdder);
        joined.ShouldBeOfType<PairingOutcome.Refused>();
        added.ShouldNotBeOfType<PairingOutcome.Joined>();
        entered.ShouldBeFalse();
        await adderEnd.DisposeAsync();
        await joinerEnd.DisposeAsync();
        await relaying;
    }

    [Fact]
    public async Task A_hello_changed_on_the_way_gives_each_side_its_own_code_and_no_frame_opens()
    {
        var (adderEnd, middleFromAdder) = FramePipe.Create();
        var (middleToJoiner, joinerEnd) = FramePipe.Create();
        string? shownOnAdder = null;
        string? shownOnJoiner = null;
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, code) => { shownOnAdder = code; return Task.CompletedTask; },
            _ => Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey(), [Member(Adder)])), Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new Broker(question => { shownOnJoiner = question.ComparisonCode; return true; }), inHousehold: false,
            enter: (_, _) => throw new InvalidOperationException("never entered"));

        // The middle keeps every key as it is and changes only the name in the joiner's hello.
        await middleToJoiner.SendAsync((await middleFromAdder.ReceiveAsync())!);
        var joinerHello = LanMessages.Read(await middleToJoiner.ReceiveAsync()).ShouldNotBeNull();
        await middleFromAdder.SendAsync(LanMessages.Write(joinerHello with { Name = "Laptop-3" }));
        var relaying = Task.WhenAll(Relay(middleFromAdder, middleToJoiner), Relay(middleToJoiner, middleFromAdder));

        (await adding).ShouldBeOfType<PairingOutcome.Failed>();
        await adderEnd.DisposeAsync();
        (await joining).ShouldBeOfType<PairingOutcome.Failed>();
        shownOnAdder.ShouldNotBeNull().ShouldNotBe(shownOnJoiner.ShouldNotBeNull());
        await joinerEnd.DisposeAsync();
        await relaying;
    }

    [Fact]
    public async Task A_frame_replayed_out_of_order_ends_the_connection()
    {
        var (adderEnd, middleFromAdder) = FramePipe.Create();
        var (middleToJoiner, joinerEnd) = FramePipe.Create();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey(), [Member(Adder)])), Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new Broker(_ => true), inHousehold: false, enter: (_, _) => Task.CompletedTask);

        await middleToJoiner.SendAsync((await middleFromAdder.ReceiveAsync())!);          // the adder's hello
        await middleFromAdder.SendAsync((await middleToJoiner.ReceiveAsync())!);          // the joiner's hello
        var answer = (await middleToJoiner.ReceiveAsync())!;
        await middleFromAdder.SendAsync(answer);
        await middleToJoiner.SendAsync((await middleFromAdder.ReceiveAsync())!);          // the welcome
        await middleToJoiner.ReceiveAsync();                                               // "joined", held back
        await middleFromAdder.SendAsync(answer);                                          // and the answer again instead

        (await adding).ShouldBeOfType<PairingOutcome.Failed>();
        await joining;
    }

    [Fact]
    public async Task A_joiner_that_doesnt_sign_its_joining_isnt_added()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new Welcome("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey(), [Member(Adder)])), Quick, CancellationToken.None);

        // A hand-made joiner that says yes and takes the welcome, then signs its joining with a key that isn't its own.
        var adderHello = (await joinerEnd.ReceiveAsync()).ShouldNotBeNull();
        var hello = PowerLedger.Service.Households.Lan.Hello.Of(LanMessages.Read(adderHello)).ShouldNotBeNull();
        using var eph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var joinerHello = LanMessages.Write(LanMessages.Hello("pair", ephPublic, Joiner.Keys, Joiner.Name, Joiner.Kind, Joiner.Instance));
        await joinerEnd.SendAsync(joinerHello);
        using var cipher = FrameCipher.For(adder: false, HouseholdCrypto.Agree(eph, hello.Eph), HouseholdCrypto.Transcript(adderHello, joinerHello));
        await joinerEnd.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage { Type = "answer", Accept = true })));
        cipher.Open((await joinerEnd.ReceiveAsync())!);
        using var other = DeviceKeys.Create();
        await joinerEnd.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage
        {
            Type = "joined", Proof = Wire.Encode(Wire.SignJoin(other, "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f")),
        })));

        (await adding).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("Laptop-2 didn't sign its joining, so it wasn't added.");
    }

    public void Dispose()
    {
        _adderKeys.Dispose();
        _joinerKeys.Dispose();
    }

    private async Task<PairingOutcome> JoinerSide(
        FramePipe end, IPromptBroker broker, bool inHousehold, Func<Welcome, MemberInfo, Task> enter)
    {
        var hello = (await end.ReceiveAsync()).ShouldNotBeNull();
        return await PairingSession.JoinAsync(end, hello, Joiner, broker, inHousehold, enter, Quick, CancellationToken.None);
    }

    private static MemberInfo Member(PairingIdentity who) => new(who.Keys.DeviceId, who.Name, who.Kind, who.Keys.SignPublic, who.Keys.DhPublic);

    private static byte[] Hello(PairingIdentity who)
    {
        using var eph = DeviceKeys.Create();
        return LanMessages.Write(LanMessages.Hello("pair", eph.DhPublic, who.Keys, who.Name, who.Kind, who.Instance));
    }

    private static byte[] SwapEph(byte[]? frame, DeviceKeys middle)
    {
        var hello = LanMessages.Read(frame).ShouldNotBeNull();
        return LanMessages.Write(hello with { Eph = Wire.Encode(middle.DhPublic) });
    }

    private static async Task Relay(FramePipe from, FramePipe to)
    {
        while (await from.ReceiveAsync() is { } frame) await to.SendAsync(frame);
        await to.DisposeAsync();
    }

    /// <summary>The user at the joining PC, answering as the test says.</summary>
    private sealed class Broker(Func<JoinQuestion, CancellationToken, Task<bool>> answer) : IPromptBroker
    {
        public Broker(Func<JoinQuestion, bool> answer) : this((question, _) => Task.FromResult(answer(question)))
        {
        }

        public Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel) => answer(question, cancel);
    }
}

/// <summary>One pairing at a time, and a pause after too many refusals (households design §3).</summary>
public sealed class PairingGateTests
{
    [Fact]
    public void Only_one_pairing_runs_at_a_time()
    {
        var gate = new PairingGate(new FakeTimeProvider());

        using var first = gate.TryEnter(out var refusal).ShouldNotBeNull();
        refusal.ShouldBeNull();
        gate.TryEnter(out refusal).ShouldBeNull();
        refusal.ShouldBe(PairingGate.Busy);

        first.Dispose();
        gate.TryEnter(out _).ShouldNotBeNull().Dispose();
    }

    [Fact]
    public void Five_refusals_in_ten_minutes_pause_pairing_for_ten_minutes()
    {
        var clock = new FakeTimeProvider();
        var gate = new PairingGate(clock);
        for (var i = 0; i < 4; i++)
        {
            gate.Refused();
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        gate.TryEnter(out _).ShouldNotBeNull().Dispose();                    // four in ten minutes

        gate.Refused();                                                        // the fifth, 8 minutes after the first
        gate.TryEnter(out var refusal).ShouldBeNull();
        refusal.ShouldBe(PairingGate.Paused);

        clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        gate.TryEnter(out _).ShouldBeNull();
        clock.Advance(TimeSpan.FromSeconds(1));
        gate.TryEnter(out _).ShouldNotBeNull().Dispose();
    }

    [Fact]
    public void Refusals_older_than_ten_minutes_no_longer_count()
    {
        var clock = new FakeTimeProvider();
        var gate = new PairingGate(clock);
        for (var i = 0; i < 4; i++) gate.Refused();
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        gate.Refused();

        gate.TryEnter(out _).ShouldNotBeNull().Dispose();
    }
}
