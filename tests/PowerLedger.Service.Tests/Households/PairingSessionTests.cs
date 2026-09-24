using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Pairing on the same network (households design §3, plan 0.8): hellos, the comparison code both users check, then
/// the welcome.</summary>
public sealed class PairingSessionTests : IDisposable
{
    private const string Hid = "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f";
    private static readonly PairingTimeouts Quick = new(Step: TimeSpan.FromSeconds(5), Answer: TimeSpan.FromSeconds(5));

    private readonly DeviceKeys _adderKeys = DeviceKeys.Create();
    private readonly DeviceKeys _joinerKeys = DeviceKeys.Create();

    private PairingIdentity Adder => new(_adderKeys, "Desktop-7", ChassisKind.Desktop, "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0");

    private PairingIdentity Joiner => new(_joinerKeys, "Laptop-2", ChassisKind.Laptop, "b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1");

    [Fact]
    public async Task Accepting_on_both_sides_joins_the_household_and_both_sides_learn_each_other()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var household = HouseholdCrypto.NewKey();
        var adderUser = new User(true);
        JoinQuestion? asked = null;
        Welcome? entered = null;
        (MemberInfo Joiner, byte[] Proof)? recorded = null;

        var adding = PairingSession.AddAsync(
            adderEnd, Adder, expectedInstance: Joiner.Instance, adderUser,
            welcomeFor: joiner => Task.FromResult<Welcome?>(new Welcome(Hid, 1, household, [Member(Adder)])),
            record: (_, joiner, proof, _) => { recorded = (joiner, proof); return Task.FromResult<string?>(null); }, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new User(question => { asked = question; return true; }), inHousehold: false,
            enter: (welcome, adder) => { entered = welcome; return Task.FromResult(true); });

        var added = (await adding).ShouldBeOfType<PairingOutcome.Joined>();
        var joined = (await joining).ShouldBeOfType<PairingOutcome.Joined>();
        Wire.IsJoinProof(added.Other, Hid, added.Proof).ShouldBeTrue();
        recorded.ShouldNotBeNull().Joiner.Id.ShouldBe(_joinerKeys.DeviceId);
        recorded.Value.Proof.ShouldBe(added.Proof);

        added.Other.Id.ShouldBe(_joinerKeys.DeviceId);
        added.Other.Name.ShouldBe("Laptop-2");
        added.Other.Kind.ShouldBe(ChassisKind.Laptop);
        added.Other.Dh.ShouldBe(_joinerKeys.DhPublic);
        joined.Other.Id.ShouldBe(_adderKeys.DeviceId);
        adderUser.Asked.ShouldBe("Laptop-2");
        asked.ShouldNotBeNull().FromName.ShouldBe("Desktop-7");
        asked.ComparisonCode.ShouldNotBeNull().ShouldMatch("^[0-9]{3} [0-9]{3}$");
        asked.ComparisonCode.ShouldBe(adderUser.Code);
        asked.LeavesHousehold.ShouldBeFalse();
        entered.ShouldNotBeNull().HouseholdId.ShouldBe(Hid);
        entered.Key.ShouldBe(household);
        entered.Members.ShouldHaveSingleItem().Sign.ShouldBe(_adderKeys.SignPublic);
    }

    [Fact]
    public async Task A_cancel_while_the_adder_waits_to_record_leaves_nothing_behind_on_either_side()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        using var stop = new CancellationTokenSource();
        var recording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;

        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(true),
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])),
            async (_, _, _, cancel) =>
            {
                recording.SetResult();
                await Task.Delay(Timeout.Infinite, cancel);                           // waiting for the household's gate
                return null;
            },
            Quick, stop.Token);
        var joining = JoinerSide(joinerEnd, new User(true), inHousehold: false, enter: (_, _) => { entered = true; return Task.FromResult(true); });
        await recording.Task;
        await stop.CancelAsync();

        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Adding Laptop-2 was cancelled.");
        (await joining).ShouldBeOfType<PairingOutcome.Failed>();
        entered.ShouldBeFalse();
    }

    [Fact]
    public async Task Once_the_adder_has_recorded_the_pairing_completes_whatever_is_cancelled_on_either_side()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        using var stopAdder = new CancellationTokenSource();
        using var stopJoiner = new CancellationTokenSource();
        Welcome? entered = null;

        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(true),
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])),
            async (_, _, _, _) =>
            {
                await stopJoiner.CancelAsync();                                       // the joiner's user cancels after it sent joined,
                await stopAdder.CancelAsync();                                        // and the adder's once it recorded
                return null;
            },
            Quick, stopAdder.Token);
        var hello = (await joinerEnd.ReceiveAsync()).ShouldNotBeNull();
        var joining = PairingSession.JoinAsync(joinerEnd, hello, Joiner, new User(true), false,
            (welcome, _) => { entered = welcome; return Task.FromResult(true); }, Quick, stopJoiner.Token);

        (await adding).ShouldBeOfType<PairingOutcome.Joined>();
        (await joining).ShouldBeOfType<PairingOutcome.Joined>();
        entered.ShouldNotBeNull().HouseholdId.ShouldBe(Hid);
    }

    [Fact]
    public async Task Refusing_changes_nothing_on_either_side_and_closes_the_adders_question()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var adderUser = new User();                                            // still looking at the code
        var welcomed = false;
        var entered = false;

        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, adderUser,
            joiner => { welcomed = true; return Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])); },
            NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new User(_ => false), inHousehold: false, enter: (_, _) => { entered = true; return Task.FromResult(true); });

        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Laptop-2 didn't join.");
        (await joining).ShouldBeOfType<PairingOutcome.Refused>();
        adderUser.Withdrawn.ShouldBeTrue();
        welcomed.ShouldBeFalse();
        entered.ShouldBeFalse();
    }

    [Fact]
    public async Task A_prompt_nobody_answers_counts_as_dont_join_and_an_answer_that_never_comes_ends_the_adding()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var clock = new FakeTimeProvider();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(true),
            _ => Task.FromException<Welcome?>(new InvalidOperationException("never welcomed")), NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new User(async (_, cancel) =>
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
        var waiting = PairingSession.AddAsync(lonelyEnd, Adder, null, new User(true),
            _ => Task.FromException<Welcome?>(new InvalidOperationException("never welcomed")), NoRecord, new PairingTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)),
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
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(true),
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new User(question => { asked = question; return false; }), inHousehold: true,
            enter: (_, _) => Task.FromResult(true));

        await adding;
        await joining;
        asked.ShouldNotBeNull().LeavesHousehold.ShouldBeTrue();
    }

    [Fact]
    public async Task A_pc_that_answers_join_gets_no_key_until_the_adders_user_confirms_the_code_and_none_once_they_cancel()
    {
        var (adderEnd, attackerEnd) = FramePipe.Create();
        using var attackerKeys = DeviceKeys.Create();
        var adderUser = new User();
        var welcomed = false;
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, adderUser,
            _ => { welcomed = true; return Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])); },
            NoRecord, Quick, CancellationToken.None);

        // Anyone who can see the joiner's announcement can answer in its place, and press Join at once.
        var hello = (await attackerEnd.ReceiveAsync()).ShouldNotBeNull();
        var cipher = Answer(attackerEnd, hello, attackerKeys, "Laptop-2", Joiner.Instance, out var sent);
        await attackerEnd.SendAsync(sent);
        LanMessages.Read(cipher.Open((await attackerEnd.ReceiveAsync())!)).ShouldNotBeNull().Type.ShouldBe("reveal");
        await attackerEnd.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage { Type = "answer", Accept = true })));

        var next = attackerEnd.ReceiveAsync();
        (await Task.WhenAny(next, Task.Delay(300))).ShouldNotBe(next);         // nothing goes while the adder's user looks at the code
        adderUser.Answer(false);                                               // the code on the real laptop is another

        LanMessages.Read(cipher.Open((await next).ShouldNotBeNull())).ShouldNotBeNull().Type.ShouldBe("cancel");
        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Adding Laptop-2 was cancelled.");
        welcomed.ShouldBeFalse();
        cipher.Dispose();
    }

    [Fact]
    public async Task The_welcome_waits_for_the_adders_user_when_the_joiners_says_yes_first()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var adderUser = new User();
        var joinerUser = new User();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, adderUser,
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, joinerUser, inHousehold: false, enter: (_, _) => Task.FromResult(true));

        await joinerUser.WaitAsked();
        joinerUser.Answer(true);
        await Task.Delay(100);
        adding.IsCompleted.ShouldBeFalse();
        joining.IsCompleted.ShouldBeFalse();
        adderUser.Answer(true);

        (await adding).ShouldBeOfType<PairingOutcome.Joined>();
        (await joining).ShouldBeOfType<PairingOutcome.Joined>();
    }

    [Fact]
    public async Task A_cancel_on_the_adder_closes_the_joiners_question()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var adderUser = new User();
        var joinerUser = new User();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, adderUser,
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, joinerUser, inHousehold: false, enter: (_, _) => throw new InvalidOperationException("never entered"));

        await joinerUser.WaitAsked();
        await adderUser.WaitAsked();
        adderUser.Answer(false);

        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Adding Laptop-2 was cancelled.");
        (await joining).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Desktop-7 stopped the pairing, so nothing was changed.");
        joinerUser.Withdrawn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_connection_that_goes_closes_the_question_on_either_side()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var joinerUser = new User();
        using var adder = new HandAdder(Adder);
        var joining = JoinerSide(joinerEnd, joinerUser, inHousehold: false, enter: (_, _) => throw new InvalidOperationException("never entered"),
            adderHello: adder.HelloBytes);
        await adder.AnswerAsync(adderEnd);
        await joinerUser.WaitAsked();
        await adderEnd.DisposeAsync();
        (await joining).ShouldBeOfType<PairingOutcome.Failed>();
        joinerUser.Withdrawn.ShouldBeTrue();

        var (otherEnd, silentEnd) = FramePipe.Create();
        var adderUser = new User();
        var adding = PairingSession.AddAsync(otherEnd, Adder, null, adderUser,
            _ => Task.FromException<Welcome?>(new InvalidOperationException("never welcomed")), NoRecord, Quick, CancellationToken.None);
        await silentEnd.ReceiveAsync();
        await silentEnd.SendAsync(Hello(Joiner));
        await adderUser.WaitAsked();
        await silentEnd.DisposeAsync();
        (await adding).ShouldBeOfType<PairingOutcome.Failed>();
        adderUser.Withdrawn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_pc_in_the_middle_that_completes_both_handshakes_is_stopped_when_the_adders_user_sees_another_code()
    {
        var (adderEnd, middleFromAdder) = FramePipe.Create();
        var (middleToJoiner, joinerEnd) = FramePipe.Create();
        using var middleKeys = DeviceKeys.Create();
        var joinerCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var welcomed = false;
        var entered = false;

        // The adder's user compares its code with the one on the laptop's screen, and cancels when they differ.
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance,
            new User(confirm: async (_, code, _) => code == await joinerCode.Task),
            _ => { welcomed = true; return Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])); },
            NoRecord, Quick, CancellationToken.None);
        // The laptop's user presses Join without a second look.
        var joining = JoinerSide(joinerEnd, new User(question => { joinerCode.TrySetResult(question.ComparisonCode!); return true; }),
            inHousehold: false, enter: (_, _) => { entered = true; return Task.FromResult(true); });

        // The middle is a joiner to the desktop and an adder to the laptop, under their names, with keys of its own: both
        // handshakes complete, and it can read and write every frame of both.
        Welcome? stolen = null;
        var toAdder = PairingSession.JoinAsync(middleFromAdder, (await middleFromAdder.ReceiveAsync())!,
            new PairingIdentity(middleKeys, "Laptop-2", ChassisKind.Laptop, Joiner.Instance), new User(true), inHousehold: false,
            (welcome, _) => { stolen = welcome; return Task.FromResult(true); }, Quick, CancellationToken.None);
        var toJoiner = PairingSession.AddAsync(middleToJoiner, new PairingIdentity(middleKeys, "Desktop-7", ChassisKind.Desktop, Adder.Instance),
            Joiner.Instance, new User(true), async _ => (await toAdder) is PairingOutcome.Joined ? stolen! : throw new InvalidOperationException("no key"),
            NoRecord, Quick, CancellationToken.None);

        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Adding Laptop-2 was cancelled.");
        (await toAdder).ShouldBeOfType<PairingOutcome.Refused>();
        stolen.ShouldBeNull();
        welcomed.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(toJoiner);
        await middleToJoiner.DisposeAsync();
        (await joining).ShouldNotBeOfType<PairingOutcome.Joined>();
        entered.ShouldBeFalse();
    }

    [Fact]
    public async Task A_hello_changed_on_the_way_gives_each_side_its_own_code_and_no_frame_opens()
    {
        var (adderEnd, middleFromAdder) = FramePipe.Create();
        var (middleToJoiner, joinerEnd) = FramePipe.Create();
        var adderUser = new User(true);
        string? shownOnJoiner = null;
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, adderUser,
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new User(question => { shownOnJoiner = question.ComparisonCode; return true; }), inHousehold: false,
            enter: (_, _) => throw new InvalidOperationException("never entered"));

        // The middle keeps every key as it is and changes only the name in the joiner's hello.
        await middleToJoiner.SendAsync((await middleFromAdder.ReceiveAsync())!);
        var joinerHello = LanMessages.Read(await middleToJoiner.ReceiveAsync()).ShouldNotBeNull();
        await middleFromAdder.SendAsync(LanMessages.Write(joinerHello with { Name = "Laptop-3" }));
        var relaying = Task.WhenAll(Relay(middleFromAdder, middleToJoiner), Relay(middleToJoiner, middleFromAdder));

        (await adding).ShouldBeOfType<PairingOutcome.Failed>();
        await adderEnd.DisposeAsync();
        (await joining).ShouldBeOfType<PairingOutcome.Failed>();
        adderUser.Code.ShouldNotBeNull();
        shownOnJoiner.ShouldBeNull();                                          // the adder's reveal never opened: the joiner wasn't even asked
        await joinerEnd.DisposeAsync();
        await relaying;
    }

    [Fact]
    public async Task A_frame_replayed_out_of_order_ends_the_connection()
    {
        var (adderEnd, middleFromAdder) = FramePipe.Create();
        var (middleToJoiner, joinerEnd) = FramePipe.Create();
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(true),
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var joining = JoinerSide(joinerEnd, new User(_ => true), inHousehold: false, enter: (_, _) => Task.FromResult(true));

        await middleToJoiner.SendAsync((await middleFromAdder.ReceiveAsync())!);          // the adder's hello
        await middleFromAdder.SendAsync((await middleToJoiner.ReceiveAsync())!);          // the joiner's hello
        await middleToJoiner.SendAsync((await middleFromAdder.ReceiveAsync())!);          // the adder's reveal
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
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(true),
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);

        // A hand-made joiner that says yes and takes the welcome, then signs its joining with a key that isn't its own.
        using var cipher = Answer(joinerEnd, (await joinerEnd.ReceiveAsync())!, Joiner.Keys, Joiner.Name, Joiner.Instance, out var sent);
        await joinerEnd.SendAsync(sent);
        await joinerEnd.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage { Type = "answer", Accept = true })));
        cipher.Open((await joinerEnd.ReceiveAsync())!);                           // the reveal
        cipher.Open((await joinerEnd.ReceiveAsync())!);                           // the welcome
        using var other = DeviceKeys.Create();
        await joinerEnd.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage { Type = "joined", Proof = Wire.Encode(Wire.SignJoin(other, Hid)) })));

        (await adding).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("Laptop-2 didn't sign its joining, so it wasn't added.");
    }

    [Fact]
    public async Task The_joiner_enters_only_once_the_adder_says_it_recorded_the_joining()
    {
        foreach (var acknowledge in new[] { false, true })
        {
            var (adderEnd, joinerEnd) = FramePipe.Create();
            var entered = false;
            using var adder = new HandAdder(Adder);
            var joining = JoinerSide(joinerEnd, new User(true), inHousehold: false, enter: (_, _) => { entered = true; return Task.FromResult(true); },
                adderHello: adder.HelloBytes);

            // A hand-made adder: the key exchange, the welcome, the joining PC's proof; then the acknowledgement, or the connection goes.
            await adder.AnswerAsync(adderEnd);
            var cipher = adder.Cipher!;
            LanMessages.Read(cipher.Open((await adderEnd.ReceiveAsync())!)).ShouldNotBeNull().Accept.ShouldBe(true);
            await adderEnd.SendAsync(cipher.Seal(LanMessages.Write(PairingSession.WelcomeMessage(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])))));
            LanMessages.Read(cipher.Open((await adderEnd.ReceiveAsync())!)).ShouldNotBeNull().Type.ShouldBe("joined");
            await Task.Delay(100);
            entered.ShouldBeFalse();                                           // staged, not entered
            if (acknowledge) await adderEnd.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage { Type = "welcomed" })));
            else await adderEnd.DisposeAsync();

            var outcome = await joining;
            entered.ShouldBe(acknowledge);
            if (acknowledge) outcome.ShouldBeOfType<PairingOutcome.Joined>();
            else outcome.ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("The connection to Desktop-7 went wrong, so nothing was changed.");
        }
    }

    [Fact]
    public async Task A_refusal_is_counted_before_the_answer_goes_and_so_is_an_adder_that_stops_while_asked()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var framesSentWhenCounted = -1;
        var adding = PairingSession.AddAsync(adderEnd, Adder, Joiner.Instance, new User(),
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var hello = (await joinerEnd.ReceiveAsync()).ShouldNotBeNull();

        var joining = PairingSession.JoinAsync(joinerEnd, hello, Joiner, new User(false), false, (_, _) => Task.FromResult(true), Quick, CancellationToken.None,
            refused: () => framesSentWhenCounted = joinerEnd.Sent.Count);

        (await joining).ShouldBeOfType<PairingOutcome.Refused>();
        framesSentWhenCounted.ShouldBe(1);                                    // its hello had gone; its answer hadn't
        joinerEnd.Sent.Count.ShouldBe(2);
        await adding;

        var (otherEnd, askedEnd) = FramePipe.Create();
        var counted = 0;
        var stillAsked = new User();
        using var adder = new HandAdder(Adder);
        var asked = PairingSession.JoinAsync(askedEnd, adder.HelloBytes, Joiner, stillAsked, false, (_, _) => Task.FromResult(true), Quick, CancellationToken.None,
            refused: () => counted++);
        await adder.AnswerAsync(otherEnd);
        await stillAsked.WaitAsked();
        await otherEnd.DisposeAsync();                                         // the adding PC went while the question was open

        (await asked).ShouldBeOfType<PairingOutcome.Failed>();
        counted.ShouldBe(1);
    }

    [Fact]
    public async Task The_code_rests_on_a_nonce_the_adder_bound_itself_to_before_the_joiners_hello_so_the_side_sending_second_cant_steer_it()
    {
        var (adderEnd, middleEnd) = FramePipe.Create();
        var adderUser = new User();
        var adding = PairingSession.AddAsync(adderEnd, Adder, null, adderUser,
            _ => Task.FromResult<Welcome?>(new Welcome(Hid, 1, HouseholdCrypto.NewKey(), [Member(Adder)])), NoRecord, Quick, CancellationToken.None);
        var adderHello = (await middleEnd.ReceiveAsync()).ShouldNotBeNull();
        var theirs = PowerLedger.Service.Households.Lan.Hello.Of(LanMessages.Read(adderHello)).ShouldNotBeNull();
        var commit = theirs.Commit.ShouldNotBeNull();

        // The PC in the middle, answering second, fixes the code it wants from its other session and may shape its hello as it
        // likes; but the code the adder shows needs the nonce, which comes only once its hello is sent.
        const string Wanted = "314 159";
        using var middleKeys = DeviceKeys.Create();
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sent = LanMessages.Write(LanMessages.Hello("pair", eph.ExportSubjectPublicKeyInfo(), middleKeys, "Laptop-2", ChassisKind.Laptop, Joiner.Instance));
        var shared = HouseholdCrypto.Agree(eph, theirs.Eph);
        var transcript = HouseholdCrypto.Transcript(adderHello, sent);
        using var cipher = FrameCipher.For(adder: false, shared, transcript);
        var first = middleEnd.ReceiveAsync();
        (await Task.WhenAny(first, Task.Delay(200))).ShouldNotBe(first);           // nothing comes before its hello
        await middleEnd.SendAsync(sent);
        var reveal = LanMessages.Read(cipher.Open((await first)!)).ShouldNotBeNull();
        reveal.Type.ShouldBe("reveal");
        var nonce = Wire.Decode(reveal.Nonce).ShouldNotBeNull();

        HouseholdCrypto.Commitment(nonce).ShouldBe(commit);                       // the adder was bound to it from its hello
        await adderUser.WaitAsked();
        adderUser.Code.ShouldBe(HouseholdCrypto.ComparisonCode(shared, transcript, nonce));
        adderUser.Code.ShouldNotBe(Wanted);                                       // except by a one-in-a-million chance
        adderUser.Answer(false);
        await adding;
    }

    [Fact]
    public async Task A_reveal_that_doesnt_match_the_commitment_ends_the_pairing_before_the_user_is_asked()
    {
        var (adderEnd, joinerEnd) = FramePipe.Create();
        var joinerUser = new User(true);
        using var adder = new HandAdder(Adder);
        var joining = JoinerSide(joinerEnd, joinerUser, inHousehold: false, enter: (_, _) => throw new InvalidOperationException("never entered"),
            adderHello: adder.HelloBytes);

        await adder.AnswerAsync(adderEnd, reveal: HouseholdCrypto.NewNonce());   // a nonce picked after seeing the joiner's hello

        (await joining).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("Desktop-7's pairing didn't hold together, so nothing was changed.");
        joinerUser.Code.ShouldBeNull();                                           // never asked
    }

    [Fact]
    public async Task An_adders_hello_without_a_commitment_isnt_answered()
    {
        var (_, joinerEnd) = FramePipe.Create();
        using var eph = DeviceKeys.Create();
        var bare = LanMessages.Write(LanMessages.Hello("pair", eph.DhPublic, Adder.Keys, Adder.Name, Adder.Kind, Adder.Instance));

        var outcome = await PairingSession.JoinAsync(joinerEnd, bare, Joiner, new User(true), false, (_, _) => Task.FromResult(true), Quick, CancellationToken.None);

        outcome.ShouldBeOfType<PairingOutcome.Failed>();
        joinerEnd.Sent.ShouldBeEmpty();                                           // not even this PC's hello
    }

    public void Dispose()
    {
        _adderKeys.Dispose();
        _joinerKeys.Dispose();
    }

    private static Task<string?> NoRecord(Welcome welcome, MemberInfo joiner, byte[] proof, CancellationToken cancel) => Task.FromResult<string?>(null);

    /// <summary>A hand-made joiner's side of the key exchange: its hello to send, and the frame keys that go with it. The
    /// first frame it then gets is the adder's reveal.</summary>
    private static FrameCipher Answer(FramePipe end, byte[] adderHello, DeviceKeys keys, string name, string instance, out byte[] hello)
    {
        var theirs = PowerLedger.Service.Households.Lan.Hello.Of(LanMessages.Read(adderHello)).ShouldNotBeNull();
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        hello = LanMessages.Write(LanMessages.Hello("pair", eph.ExportSubjectPublicKeyInfo(), keys, name, ChassisKind.Laptop, instance));
        return FrameCipher.For(adder: false, HouseholdCrypto.Agree(eph, theirs.Eph), HouseholdCrypto.Transcript(adderHello, hello));
    }

    /// <summary>A hand-made adder: a pair hello committed to a nonce, then, once the joiner's hello comes, the frame keys and
    /// the reveal.</summary>
    private sealed class HandAdder : IDisposable
    {
        private readonly ECDiffieHellman _eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public HandAdder(PairingIdentity who)
        {
            HelloBytes = LanMessages.Write(LanMessages.Hello("pair", _eph.ExportSubjectPublicKeyInfo(), who.Keys, who.Name, who.Kind, who.Instance) with
            {
                Commit = Wire.Encode(HouseholdCrypto.Commitment(Nonce)),
            });
        }

        public byte[] Nonce { get; } = HouseholdCrypto.NewNonce();

        public byte[] HelloBytes { get; }

        public FrameCipher? Cipher { get; private set; }

        /// <summary>Reads the joiner's hello and sends the reveal: the committed nonce, or <paramref name="reveal"/> in its place.</summary>
        public async Task AnswerAsync(FramePipe end, byte[]? reveal = null)
        {
            var joinerHello = (await end.ReceiveAsync()).ShouldNotBeNull();
            var theirs = PowerLedger.Service.Households.Lan.Hello.Of(LanMessages.Read(joinerHello)).ShouldNotBeNull();
            Cipher = FrameCipher.For(adder: true, HouseholdCrypto.Agree(_eph, theirs.Eph), HouseholdCrypto.Transcript(HelloBytes, joinerHello));
            await end.SendAsync(Cipher.Seal(LanMessages.Write(new LanMessage { Type = "reveal", Nonce = Wire.Encode(reveal ?? Nonce) })));
        }

        public void Dispose()
        {
            Cipher?.Dispose();
            _eph.Dispose();
        }
    }

    private async Task<PairingOutcome> JoinerSide(
        FramePipe end, IPromptBroker broker, bool inHousehold, Func<Welcome, MemberInfo, Task<bool>> enter, byte[]? adderHello = null)
    {
        var hello = adderHello ?? (await end.ReceiveAsync()).ShouldNotBeNull();
        return await PairingSession.JoinAsync(end, hello, Joiner, broker, inHousehold, enter, Quick, CancellationToken.None);
    }

    private static MemberInfo Member(PairingIdentity who) => new(who.Keys.DeviceId, who.Name, who.Kind, who.Keys.SignPublic, who.Keys.DhPublic);

    private static byte[] Hello(PairingIdentity who)
    {
        using var eph = DeviceKeys.Create();
        return LanMessages.Write(LanMessages.Hello("pair", eph.DhPublic, who.Keys, who.Name, who.Kind, who.Instance));
    }

    private static async Task Relay(FramePipe from, FramePipe to)
    {
        while (await from.ReceiveAsync() is { } frame) await to.SendAsync(frame);
        await to.DisposeAsync();
    }

    /// <summary>The user at one PC. Answers at once as the test says, or waits for <see cref="Answer"/>; notes a question withdrawn.</summary>
    private sealed class User : IPromptBroker
    {
        private readonly Func<JoinQuestion, CancellationToken, Task<bool>>? _join;
        private readonly Func<string, string, CancellationToken, Task<bool>>? _confirm;
        private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _withdrawn;

        /// <summary>A user who answers when the test says.</summary>
        public User()
        {
        }

        /// <summary>A user who answers every question with <paramref name="always"/> at once.</summary>
        public User(bool always) => _answer.SetResult(always);

        public User(Func<JoinQuestion, bool> join) => _join = (question, _) => Task.FromResult(join(question));

        public User(Func<JoinQuestion, CancellationToken, Task<bool>> join) => _join = join;

        public User(Func<string, string, CancellationToken, Task<bool>> confirm) => _confirm = confirm;

        /// <summary>The other PC's name, as the adder's user was asked about it.</summary>
        public string? Asked { get; private set; }

        /// <summary>The code this PC showed.</summary>
        public string? Code { get; private set; }

        public bool Withdrawn => Volatile.Read(ref _withdrawn) == 1;

        public void Answer(bool yes) => _answer.TrySetResult(yes);

        public Task WaitAsked() => _asked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel)
        {
            Code = question.ComparisonCode;
            return Wait(_join is null ? null : () => _join(question, cancel), cancel);
        }

        public Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel)
        {
            (Asked, Code) = (otherName, code);
            return Wait(_confirm is null ? null : () => _confirm(otherName, code, cancel), cancel);
        }

        private async Task<bool> Wait(Func<Task<bool>>? given, CancellationToken cancel)
        {
            _asked.TrySetResult();
            try
            {
                return await (given?.Invoke() ?? _answer.Task).WaitAsync(cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Volatile.Write(ref _withdrawn, 1);
                return false;
            }
        }
    }
}

/// <summary>One pairing at a time, and a pause after too many refusals (households design §3).</summary>
public sealed class PairingGateTests
{
    [Fact]
    public void Three_pairings_that_come_to_nothing_from_one_address_leave_it_unanswered_for_ten_minutes_and_ipv6_counts_by_its_64()
    {
        var clock = new FakeTimeProvider();
        var strangers = new StrangerGate(clock);
        var noisy = System.Net.IPAddress.Parse("192.168.1.66");
        var other = System.Net.IPAddress.Parse("192.168.1.7");

        for (var i = 0; i < StrangerGate.PerAddress - 1; i++) strangers.Failed(noisy);
        strangers.Allowed(noisy).ShouldBeTrue();
        strangers.Failed(noisy);
        strangers.Allowed(noisy).ShouldBeFalse();
        strangers.Allowed(other).ShouldBeTrue();

        clock.Advance(StrangerGate.Pause);
        strangers.Allowed(noisy).ShouldBeTrue();
        strangers.Failed(noisy);                                               // counting starts again
        strangers.Allowed(noisy).ShouldBeTrue();

        // One home's IPv6 /64 is one address, however many addresses it takes from it.
        strangers.Failed(System.Net.IPAddress.Parse("2001:db8:1:2::10"));
        strangers.Failed(System.Net.IPAddress.Parse("2001:db8:1:2:aaaa::11"));
        strangers.Failed(System.Net.IPAddress.Parse("2001:db8:1:2:bbbb:cccc:dddd:12"));
        strangers.Allowed(System.Net.IPAddress.Parse("2001:db8:1:2::99")).ShouldBeFalse();
        strangers.Allowed(System.Net.IPAddress.Parse("2001:db8:1:3::10")).ShouldBeTrue();
    }

    [Fact]
    public void Twenty_from_all_addresses_together_pause_every_pairing_from_the_network()
    {
        var clock = new FakeTimeProvider();
        var strangers = new StrangerGate(clock);

        for (var i = 0; i < StrangerGate.AllAddresses; i++) strangers.Failed(System.Net.IPAddress.Parse($"10.0.{i}.1"));

        strangers.Allowed(System.Net.IPAddress.Parse("10.9.9.9")).ShouldBeFalse();
        clock.Advance(StrangerGate.Pause);
        strangers.Allowed(System.Net.IPAddress.Parse("10.9.9.9")).ShouldBeTrue();
    }

    [Fact]
    public void A_full_table_keeps_its_paused_addresses_and_while_it_holds_nothing_else_a_new_address_goes_unanswered()
    {
        var clock = new FakeTimeProvider();
        var strangers = new StrangerGate(clock, allAddresses: int.MaxValue, maxAddresses: 4);
        var addresses = Enumerable.Range(1, 4).Select(i => System.Net.IPAddress.Parse($"10.0.0.{i}")).ToList();
        foreach (var address in addresses)
        {
            for (var i = 0; i < StrangerGate.PerAddress; i++) strangers.Failed(address);
        }

        var newcomer = System.Net.IPAddress.Parse("172.16.0.1");
        strangers.Allowed(newcomer).ShouldBeFalse();                           // the table is all paused addresses
        strangers.Failed(newcomer);
        addresses.ShouldAllBe(address => !strangers.Allowed(address));         // none pushed out to make room
        clock.Advance(StrangerGate.Pause);
        strangers.Allowed(newcomer).ShouldBeTrue();                            // room again once they aren't paused
    }

    [Fact]
    public void At_most_three_notices_about_pairings_from_the_network_go_in_ten_minutes()
    {
        var clock = new FakeTimeProvider();
        var strangers = new StrangerGate(clock);

        Enumerable.Range(0, 5).Select(_ => strangers.MayTell()).ShouldBe([true, true, true, false, false]);
        clock.Advance(PairingGate.Window + TimeSpan.FromSeconds(1));
        strangers.MayTell().ShouldBeTrue();
    }

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
