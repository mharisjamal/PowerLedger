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

        var adding = _pairing.AddAsync(meeting, Adder, Welcome, CancellationToken.None);
        var joined = await _pairing.JoinAsync(meeting.Code.ToLowerInvariant().Replace("-", " "), Joiner,
            new Broker(question => { asked = question; return true; }), inHousehold: true,
            enter: (welcome, _) => { entered = welcome; return Task.CompletedTask; }, CancellationToken.None);
        var added = await adding;

        joined.ShouldBeOfType<PairingOutcome.Joined>().Other.Id.ShouldBe(_adderKeys.DeviceId);
        added.ShouldBeOfType<PairingOutcome.Joined>().Other.Id.ShouldBe(_joinerKeys.DeviceId);
        added.Text.ShouldBe("Laptop-2 joined your household.");
        asked.ShouldNotBeNull().ComparisonCode.ShouldBeNull();
        (asked.FromName, asked.LeavesHousehold).ShouldBe(("Desktop-7", true));
        entered.ShouldNotBeNull().HouseholdId.ShouldBe(Household);
        entered.Key.ShouldBe(_key);
        var meetingId = PairingCode.MeetingId(PairingCode.Normalize(meeting.Code)!);
        _relay.Calls.ShouldContain($"PUT /v1/meetings/{meetingId}/welcome");
        _relay.Calls.ShouldAllBe(call => !call.Contains(meeting.Code.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Saying_no_changes_nothing_on_either_side()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        var welcomed = false;

        var adding = _pairing.AddAsync(meeting, Adder, joiner => { welcomed = true; return Welcome(joiner); }, CancellationToken.None);
        var joined = await _pairing.JoinAsync(meeting.Code, Joiner, new Broker(_ => false), inHousehold: false,
            enter: (_, _) => throw new InvalidOperationException("never entered"), CancellationToken.None);

        joined.ShouldBeOfType<PairingOutcome.Refused>();
        (await adding).ShouldBeOfType<PairingOutcome.Refused>().Text.ShouldBe("Laptop-2 didn't join.");
        welcomed.ShouldBeFalse();
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

        var adding = _pairing.AddAsync(meeting, Adder, Welcome, CancellationToken.None);
        _relay.PutSlot(meetingId, "joiner", ForgedHello(Joiner, PairingCode.Key(forged)));
        (await adding).ShouldBeOfType<PairingOutcome.Failed>().Text.ShouldBe("A PC tried the code, but its keys didn't match it, so it wasn't added.");
    }

    [Fact]
    public async Task A_code_nobody_uses_runs_out_after_ten_minutes()
    {
        using var meeting = (await _pairing.OpenAsync(Adder, CancellationToken.None)).ShouldNotBeNull();
        var adding = _pairing.AddAsync(meeting, Adder, Welcome, CancellationToken.None);

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

    public void Dispose()
    {
        _client.Dispose();
        _adderKeys.Dispose();
        _joinerKeys.Dispose();
    }

    private Welcome Welcome(MemberInfo joiner) =>
        new(Household, 1, _key, [new MemberInfo(_adderKeys.DeviceId, "Desktop-7", ChassisKind.Desktop, _adderKeys.SignPublic, _adderKeys.DhPublic)]);

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
    }
}
