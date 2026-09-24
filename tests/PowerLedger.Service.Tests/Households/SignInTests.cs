using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>N2's sign-in (households design §7): the session kept, the household linked with a recovery code given once, a
/// PC outside the household asking to join, and recovering with the code.</summary>
public sealed class SignInTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeNetwork _network = new();
    private readonly FakeRelay _relay;
    private readonly List<WorkerPc> _pcs = [];

    public SignInTests() => _relay = new FakeRelay(_clock);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var pc in _pcs) await pc.DisposeAsync();
    }

    [Fact]
    public async Task Signing_in_on_a_pc_in_a_household_links_it_to_the_account_and_gives_a_recovery_code_once()
    {
        var (desktop, _) = await Household();
        var household = desktop.Worker.Store.HouseholdId!;

        var reply = await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));

        (reply.Ok, reply.Message).ShouldBe((true, "Signed in, and your household is linked to your account."));
        var code = reply.Code.ShouldNotBeNull();
        code.ShouldMatch("^([0-9A-Z]{4}-){5}[0-9A-Z]{4}$");
        desktop.Worker.Store.Session.ShouldNotBeNull();
        desktop.Board.Household!.SignedIn.ShouldBeTrue();
        _relay.LinkOf("alice").ShouldBe(household);
        var envelope = _relay.RecoveryOf("alice").ShouldNotBeNull();
        envelope.Epoch.ShouldBe(1);
        var opened = Recovery.Open(RecoveryCode.Key(RecoveryCode.Normalize(code)!), new RecoveryReply(household, 1, envelope.Body));
        opened.ShouldBe(desktop.Worker.Store.CurrentKey);
        envelope.Verifier.ShouldBe(Wire.Encode(Recovery.Verifier(opened!)));

        var again = await desktop.Send<HouseholdReply>(SignIn(desktop, "alice", salt: "another"));
        (again.Ok, again.Message, again.Code).ShouldBe((true, "Signed in.", (string?)null));
    }

    [Fact]
    public async Task Signing_in_on_a_pc_outside_the_accounts_household_asks_to_join_it()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));
        var study = await Start("Study PC");

        var reply = await study.Send<HouseholdReply>(SignIn(study, "alice"));

        reply.ShouldBe(new HouseholdReply(7, true, HouseholdWorker.WaitingForApproval));
        _relay.Waiting(desktop.Worker.Store.HouseholdId!).ShouldBe([study.Worker.DeviceId]);
        study.Worker.Store.AskedToJoin.ShouldBe(desktop.Worker.Store.HouseholdId);
        study.Worker.Store.HouseholdId.ShouldBeNull();
    }

    [Fact]
    public async Task The_recovery_code_brings_the_household_back_on_a_new_pc_without_approval()
    {
        var (desktop, _) = await Household();
        var code = (await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"))).Code!;
        desktop.Household.Upsert([new HouseholdRow(desktop.Worker.DeviceId, Now.AddHours(-3).ToUnixTimeMilliseconds(), 12, 1, 1, 1, 1, 0, 0,
            3600, 0, 0, 3600, 0, 0, 3_000, "GBP", Now.ToUnixTimeMilliseconds() + 1)]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        var fresh = await Start("New laptop");

        var reply = await fresh.Send<HouseholdReply>(SignIn(fresh, "alice", recovery: code.ToLowerInvariant().Replace('-', ' ')));

        reply.ShouldBe(new HouseholdReply(7, true, "Your household is back on this PC."));
        fresh.Worker.Store.HouseholdId.ShouldBe(desktop.Worker.Store.HouseholdId);
        fresh.Worker.Store.CurrentKey.ShouldBe(desktop.Worker.Store.CurrentKey);
        _relay.Members(fresh.Worker.Store.HouseholdId!).Keys.ShouldContain(fresh.Worker.DeviceId);

        await fresh.Worker.RunOnceAsync(CancellationToken.None);
        fresh.Household.Member(desktop.Worker.DeviceId).ShouldNotBeNull().Name.ShouldBe("Desktop-7");
        fresh.Household.RowsBetween(desktop.Worker.DeviceId, 0, long.MaxValue).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_wrong_recovery_code_is_refused_and_leaves_the_pc_as_it_was()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));
        var fresh = await Start("New laptop");

        var reply = await fresh.Send<HouseholdReply>(SignIn(fresh, "alice", recovery: RecoveryCode.New()));

        reply.ShouldBe(new HouseholdReply(7, false, "That recovery code doesn't open your account's household."));
        fresh.Worker.Store.HouseholdId.ShouldBeNull();
        (await fresh.Send<HouseholdReply>(SignIn(fresh, "alice", recovery: "not a code"))).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_made_for_another_pc_is_refused_and_no_session_is_kept()
    {
        var desktop = await Start("Desktop-7");
        var salt = "s1";
        var stolen = new SignInRequest(7, "microsoft", FakeRelay.IdToken("microsoft", "alice", "0123456789abcdef0123456789abcdef", salt), salt);

        var reply = await desktop.Send<HouseholdReply>(stolen);

        reply.ShouldBe(new HouseholdReply(7, false, "Couldn't sign in: The ID token's nonce doesn't match."));
        desktop.Worker.Store.Session.ShouldBeNull();
        desktop.Board.Household!.SignedIn.ShouldBeFalse();
        (await desktop.Send<HouseholdReply>(new SignInRequest(8, "facebook", "x", "y"))).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_in_on_a_pc_in_no_household_with_an_unlinked_account_just_signs_in()
    {
        var desktop = await Start("Desktop-7");

        (await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"))).ShouldBe(new HouseholdReply(7, true, "Signed in."));
        _relay.LinkOf("alice").ShouldBeNull();
    }

    private static SignInRequest SignIn(WorkerPc pc, string subject, string salt = "salt-1", string? recovery = null) =>
        new(7, "microsoft", FakeRelay.IdToken("microsoft", subject, pc.Worker.DeviceId, salt), salt, recovery);

    private async Task<WorkerPc> Start(string name, ChassisKind kind = ChassisKind.Laptop)
    {
        var pc = new WorkerPc(name, kind, _network, _relay, _clock, appAtTheScreen: true);
        _pcs.Add(pc);
        await pc.Worker.StartAsync(CancellationToken.None);
        return pc;
    }

    /// <summary>A desktop and a laptop paired on the network, the server told.</summary>
    private async Task<(WorkerPc Desktop, WorkerPc Laptop)> Household()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2");
        await WorkerPc.Pair(desktop, laptop);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        return (desktop, laptop);
    }
}
