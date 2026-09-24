using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Service.Households.Relay;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Pairing through the server with a one-time code (households design §4): the meeting's slots, each side's MAC under
/// the code's key, the prompt without a comparison code, and the sealed answer and welcome.</summary>
public sealed class CodePairingTests : IDisposable
{
    private const string Household = "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeRelay _relay;
    private readonly RelayClient _client;
    private readonly CodePairing _pairing;
    private readonly DeviceKeys _adderKeys = DeviceKeys.Create();
    private readonly DeviceKeys _joinerKeys = DeviceKeys.Create();
    private readonly byte[] _key = HouseholdCrypto.NewKey();

    public CodePairingTests()
    {
        _relay = new FakeRelay(_clock);
        _client = new RelayClient(FakeRelay.Endpoint, _clock, _relay);
        _pairing = new CodePairing(_client, _clock, wait: (_, cancel) => Task.Delay(5, cancel));
    }

    private PairingIdentity Adder => new(_adderKeys, "Desktop-7", ChassisKind.Desktop, "a0");

    private PairingIdentity Joiner => new(_joinerKeys, "Laptop-2", ChassisKind.Laptop, "b1");

    [Fact]
    public async Task A_good_code_joins_without_a_comparison_code()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        meeting.Code.ShouldMatch("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$");
        JoinQuestion? asked = null;
        Welcome? entered = null;

        var adding = _pairing.AddAsync(meeting, Adder, Welcome, NoRecord, CancellationToken.None);
        var joined = await _pairing.JoinAsync(meeting.Code.ToLowerInvariant().Replace("-", " "), Joiner,
            new Broker(question => { asked = question; return true; }), inHousehold: true,
            enter: (welcome, _) => { entered = welcome; return Task.CompletedTask; }, CancellationToken.None);
        var added = await adding;

        joined.ShouldBeOfType<PairingOutcome.Joined>().Other.Id.ShouldBe(_adderKeys.DeviceId);
        joined.Text.ShouldBe("This PC joined Desktop-7's household.");
        ((PairingOutcome.Joined)joined).Other.Name.ShouldBe("Desktop-7");                 // from the sealed welcome
        added.ShouldBeOfType<PairingOutcome.Joined>().Other.Id.ShouldBe(_joinerKeys.DeviceId);
        ((PairingOutcome.Joined)added).Other.Name.ShouldBe("Laptop-2");                   // from the sealed answer
        ((PairingOutcome.Joined)added).Other.Kind.ShouldBe(ChassisKind.Laptop);
        Wire.IsJoinProof(((PairingOutcome.Joined)added).Other, Household, ((PairingOutcome.Joined)added).Proof).ShouldBeTrue();
        added.Text.ShouldBe("Laptop-2 joined your household.");
        asked.ShouldNotBeNull().ComparisonCode.ShouldBeNull();
        (asked.FromName, asked.LeavesHousehold).ShouldBe(((string?)null, true));
        entered.ShouldNotBeNull().HouseholdId.ShouldBe(Household);
        entered.Key.ShouldBe(_key);
        var meetingId = PairingCode.MeetingId(PairingCode.Normalize(meeting.Code)!);
        _relay.Calls.ShouldContain($"PUT /v1/meetings/{meetingId}/welcome");
        _relay.Calls.ShouldContain($"PUT /v1/meetings/{meetingId}/welcomed");
        _relay.Calls.ShouldAllBe(call => !call.Contains(meeting.Code.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

        // What the server holds in the open is keys and MACs: no name, kind or instance.
        foreach (var slot in new[] { "adder", "joiner" })
        {
            var held = LanMessages.Read(_relay.Slot(meetingId, slot)).ShouldNotBeNull();
            (held.Name, held.Kind, held.Instance).ShouldBe(((string?)null, (string?)null, (string?)null));
            held.Mac.ShouldNotBeNull();
            System.Text.Encoding.UTF8.GetString(_relay.Slot(meetingId, slot)!).ShouldNotContain(slot == "adder" ? "Desktop-7" : "Laptop-2");
        }
    }

    [Fact]
    public async Task Saying_no_changes_nothing_on_either_side()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        var welcomed = false;

        var adding = _pairing.AddAsync(meeting, Adder, joiner => { welcomed = true; return Welcome(joiner); }, NoRecord, CancellationToken.None);
        var joined = await _pairing.JoinAsync(meeting.Code, Joiner, new Broker(_ => false), inHousehold: false,
            enter: (_, _) => throw new InvalidOperationException("never entered"), CancellationToken.None);

        joined.ShouldBeOfType<PairingOutcome.Refused>();
        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("The other PC didn't join.");
        welcomed.ShouldBeFalse();
    }

    [Fact]
    public async Task The_joiner_enters_only_once_the_adder_has_recorded_it_and_written_welcomed()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        var entered = false;

        var adding = _pairing.AddAsync(meeting, Adder, Welcome, (_, _) => throw new TimeoutException("the household stayed busy"), CancellationToken.None);
        var joining = _pairing.JoinAsync(meeting.Code, Joiner, new Broker(_ => true), inHousehold: false,
            enter: (_, _) => { entered = true; return Task.CompletedTask; }, CancellationToken.None);
        await Should.ThrowAsync<TimeoutException>(adding);                    // the adder couldn't record it, so never said welcomed

        await WaitFor.True(() => _relay.Calls.Any(call => call.EndsWith("/welcomed", StringComparison.Ordinal)));
        _clock.Advance(CodePairing.Lifetime);
        (await joining).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("Desktop-7 didn't finish adding this PC in time, so nothing was changed.");
        entered.ShouldBeFalse();
    }

    [Fact]
    public async Task A_wrong_code_finds_no_one_and_a_slot_that_fails_the_mac_is_refused()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        var wrong = PairingCode.New();

        (await _pairing.JoinAsync(wrong, Joiner, new Broker(_ => true), false, (_, _) => Task.CompletedTask, CancellationToken.None))
            .ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("No PC is waiting with that code. Check it, or make a new one on the other PC.");
        (await _pairing.JoinAsync("not a code", Joiner, new Broker(_ => true), false, (_, _) => Task.CompletedTask, CancellationToken.None))
            .ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldStartWith("That isn't a code");

        // Someone who knows the meeting but not the code: its MAC is made under another key.
        var forged = PairingCode.Normalize(PairingCode.New())!;
        var normalized = PairingCode.Normalize(meeting.Code)!;
        var meetingId = PairingCode.MeetingId(normalized);
        var asked = false;
        var planted = PairingCode.MeetingId(forged);
        _relay.PutSlot(planted, "adder", ForgedHello(Adder, PairingCode.Key(normalized)));
        (await _pairing.JoinAsync(forged, Joiner, new Broker(_ => { asked = true; return true; }), false, (_, _) => Task.CompletedTask, CancellationToken.None))
            .ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("That code doesn't match the other PC's, so nothing was changed.");
        asked.ShouldBeFalse();

        var adding = _pairing.AddAsync(meeting, Adder, Welcome, NoRecord, CancellationToken.None);
        _relay.PutSlot(meetingId, "joiner", ForgedHello(Joiner, PairingCode.Key(forged)));
        (await adding).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("A PC tried the code, but its keys didn't match it, so it wasn't added.");
    }

    [Fact]
    public async Task A_code_nobody_uses_runs_out_after_ten_minutes()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        var adding = _pairing.AddAsync(meeting, Adder, Welcome, NoRecord, CancellationToken.None);

        await WaitFor.True(() => _relay.Posted("GET") >= 2);
        _clock.Advance(CodePairing.Lifetime);

        (await adding).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("The code ran out before another PC used it.");
        (await _pairing.JoinAsync(meeting.Code, Joiner, new Broker(_ => true), false, (_, _) => Task.CompletedTask, CancellationToken.None))
            .ShouldBeOfType<PairingOutcome.Failed>();
    }

    [Fact]
    public async Task No_server_means_no_code()
    {
        _relay.Down = true;

        (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_side_that_stops_says_so_in_the_slot_the_other_waits_on()
    {
        // The adder stops after the joiner said yes: its goodbye goes where the welcome would have.
        using (var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull())
        {
            using var stopAdder = new CancellationTokenSource();
            var adding = _pairing.AddAsync(meeting, Adder, async joiner =>
            {
                await stopAdder.CancelAsync();
                stopAdder.Token.ThrowIfCancellationRequested();
                return await Welcome(joiner);
            }, NoRecord, stopAdder.Token);
            var joined = await _pairing.JoinAsync(meeting.Code, Joiner, new Broker(_ => true), false, (_, _) => Task.CompletedTask, CancellationToken.None);

            (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Adding the other PC was cancelled.");
            joined.ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("The other PC stopped the pairing, so nothing was changed.");
        }

        // The joiner stops while its user is still asked: its goodbye goes where the answer would have.
        using (var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull())
        {
            using var stopJoiner = new CancellationTokenSource();
            var adding = _pairing.AddAsync(meeting, Adder, Welcome, NoRecord, CancellationToken.None);
            var joined = _pairing.JoinAsync(meeting.Code, Joiner, new Waiting(), false, (_, _) => Task.CompletedTask, stopJoiner.Token);
            await WaitFor.True(() => _relay.Calls.Any(call => call.EndsWith("/joiner", StringComparison.Ordinal) && call.StartsWith("PUT", StringComparison.Ordinal)));
            await stopJoiner.CancelAsync();

            (await joined).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("This PC stopped the pairing, so nothing was changed.");
            (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("The other PC stopped the pairing.");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _adderKeys.Dispose();
        _joinerKeys.Dispose();
    }

    private static Task NoRecord(MemberInfo joiner, byte[] proof) => Task.CompletedTask;

    private Task<Welcome> Welcome(MemberInfo joiner) => Task.FromResult(
        new Welcome(Household, 1, _key, [new MemberInfo(_adderKeys.DeviceId, "Desktop-7", ChassisKind.Desktop, _adderKeys.SignPublic, _adderKeys.DhPublic)]));

    /// <summary>A hello for a meeting slot, with a MAC under a key that isn't the code's.</summary>
    private static byte[] ForgedHello(PairingIdentity who, byte[] wrongKey)
    {
        using var eph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var hello = LanMessages.Hello("pair", ephPublic, who.Keys, who.Name, who.Kind, who.Instance) with
        {
            Mac = Wire.Encode(PairingCode.Mac(wrongKey, "adder", ephPublic, who.Keys.SignPublic, who.Keys.DhPublic)),
        };
        return LanMessages.Write(hello);
    }

    private sealed class Broker(Func<JoinQuestion, bool> answer) : IPromptBroker
    {
        public Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel) => Task.FromResult(answer(question));

        public Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel) => Task.FromResult(false);
    }

    /// <summary>A user who hasn't answered yet: the question stays open until it is withdrawn.</summary>
    private sealed class Waiting : IPromptBroker
    {
        public async Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            catch (OperationCanceledException)
            {
            }
            return false;
        }

        public Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel) => AskToJoinAsync(null!, cancel);
    }
}
