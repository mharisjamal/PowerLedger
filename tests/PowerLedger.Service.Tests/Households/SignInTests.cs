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
        var (desktop, laptop) = await Household();
        var household = desktop.Worker.Store.HouseholdId!;

        var reply = await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));

        (reply.Ok, reply.Message, reply.Code).ShouldBe((true, "Signed in, and your household is linked to your account.", (string?)null));
        var shown = await desktop.Next(NoticeKind.RecoveryCode);                  // the code comes as a notice, shown once
        shown.Text.ShouldBe(HouseholdWorker.KeepTheCode);
        var code = shown.RecoveryCode.ShouldNotBeNull();
        code.ShouldMatch("^([0-9A-Z]{4}-){5}[0-9A-Z]{4}$");
        desktop.Worker.Store.RecoveryCodeToShow.ShouldNotBeNull().Code.ShouldBe(code);   // kept, encrypted, until it was seen
        (await desktop.Send<HouseholdReply>(new AnswerPromptRequest(8, shown.PromptId!, true))).Ok.ShouldBeTrue();
        desktop.Worker.Store.RecoveryCodeToShow.ShouldBeNull();
        desktop.Worker.Store.Session.ShouldNotBeNull();
        desktop.Board.Household!.SignedIn.ShouldBeTrue();
        _relay.LinkOf("alice").ShouldBe(household);
        var envelope = _relay.RecoveryOf("alice").ShouldNotBeNull();
        (envelope.Epoch, envelope.Holder).ShouldBe((1, desktop.Worker.DeviceId));   // this PC holds the code
        var codeKey = RecoveryCode.Key(RecoveryCode.Normalize(code)!);
        var opened = Recovery.Open(codeKey, household, new RecoveryReply(envelope.Body, 1, envelope.Holder)).ShouldNotBeNull();
        opened.Key.ShouldBe(desktop.Worker.Store.CurrentKey);
        opened.Members.Select(member => member.Id).ShouldContain(laptop.Worker.DeviceId);   // with the member list
        envelope.Verifier.ShouldBe(Wire.Encode(Recovery.Verifier(codeKey)));        // made from the code alone

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
    public async Task The_recovery_code_brings_the_household_back_on_a_new_pc_as_its_only_member_and_is_used_up()
    {
        var (desktop, laptop) = await Household();
        var household = desktop.Worker.Store.HouseholdId!;
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));
        var code = (await desktop.Next(NoticeKind.RecoveryCode)).RecoveryCode!;
        desktop.Household.Upsert([new HouseholdRow(desktop.Worker.DeviceId, Now.AddHours(-3).ToUnixTimeMilliseconds(), 12, 1, 1, 1, 1, 0, 0,
            3600, 0, 0, 3600, 0, 0, 3_000, "GBP", Now.ToUnixTimeMilliseconds() + 1)]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        var oldKey = desktop.Worker.Store.CurrentKey;
        var fresh = await Start("New laptop");

        var reply = await fresh.Send<HouseholdReply>(SignIn(fresh, "alice", recovery: code.ToLowerInvariant().Replace('-', ' ')));

        reply.ShouldBe(new HouseholdReply(7, true, "Your household is back on this PC."));
        fresh.Worker.Store.HouseholdId.ShouldBe(household);
        var members = _relay.Members(household);
        members[fresh.Worker.DeviceId].Removed.ShouldBeNull();                       // the only current member,
        members[desktop.Worker.DeviceId].Removed.ShouldNotBeNull();                  // every other removed
        members[laptop.Worker.DeviceId].Removed.ShouldNotBeNull();
        fresh.Household.Member(desktop.Worker.DeviceId).ShouldNotBeNull().LeftMs.ShouldNotBeNull();   // known from the recovery's list
        var newCode = (await fresh.Next(NoticeKind.RecoveryCode)).RecoveryCode.ShouldNotBeNull();      // the code is used up: a new one
        newCode.ShouldNotBe(code);
        fresh.Worker.Store.RecoveryKey.ShouldNotBe(RecoveryCode.Key(RecoveryCode.Normalize(code)!));   // the old one isn't kept

        await fresh.Worker.RunOnceAsync(CancellationToken.None);

        fresh.Worker.Store.Epoch.ShouldBe(2);                                        // a new key, without the others
        fresh.Worker.Store.CurrentKey.ShouldNotBe(oldKey);
        _relay.Sealed(household, 2).ShouldBe([fresh.Worker.DeviceId]);
        var recovery = _relay.RecoveryOf("alice").ShouldNotBeNull();
        (recovery.Epoch, recovery.Holder).ShouldBe((2, fresh.Worker.DeviceId));
        Recovery.Open(RecoveryCode.Key(RecoveryCode.Normalize(newCode)!), household, new RecoveryReply(recovery.Body, 2, recovery.Holder))
            .ShouldNotBeNull().Key.ShouldBe(fresh.Worker.Store.CurrentKey);
        fresh.Household.RowsBetween(desktop.Worker.DeviceId, 0, long.MaxValue).ShouldNotBeEmpty();   // what it posted before it went
        var other = await Start("Another PC");
        (await other.Send<HouseholdReply>(SignIn(other, "alice", salt: "o1", recovery: code))).ShouldBe(
            new HouseholdReply(7, false, "That recovery code doesn't open your account's household."));
        (await desktop.Worker.RunOnceAsync(CancellationToken.None).ContinueWith(_ => desktop.Worker.Store.HouseholdId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_recovery_code_sign_in_on_a_pc_in_another_household_is_refused_like_one_linked_elsewhere()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));
        var code = (await desktop.Next(NoticeKind.RecoveryCode)).RecoveryCode!;
        var elsewhere = await Start("Study PC");
        var other = await Start("Other PC");
        await WorkerPc.Pair(elsewhere, other);

        var reply = await elsewhere.Send<HouseholdReply>(SignIn(elsewhere, "alice", recovery: code));

        reply.ShouldBe(new HouseholdReply(7, false, HouseholdWorker.LinkedElsewhere));
        elsewhere.Worker.Store.HouseholdId.ShouldNotBe(desktop.Worker.Store.HouseholdId);
        elsewhere.Worker.Store.Session.ShouldBeNull();
        _relay.Members(desktop.Worker.Store.HouseholdId!)[desktop.Worker.DeviceId].Removed.ShouldBeNull();
        _relay.RecoveryOf("alice").ShouldNotBeNull();                               // not used up
    }

    [Fact]
    public async Task A_pc_holding_an_older_code_never_overwrites_a_newer_one_and_forgets_its_own()
    {
        var (desktop, laptop) = await Household();
        var household = desktop.Worker.Store.HouseholdId!;
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));                 // the desktop holds the first code
        await laptop.Send<HouseholdReply>(SignIn(laptop, "alice", salt: "l1"));
        laptop.Worker.Store.RecoveryKey.ShouldBeNull();                               // signing in with a code linked keeps none

        (await laptop.Send<HouseholdReply>(new NewRecoveryCodeRequest(8))).Ok.ShouldBeTrue();
        var newer = (await laptop.Next(NoticeKind.RecoveryCode)).RecoveryCode!;
        desktop.Worker.Store.AddPending(new PendingOp(PendingOp.RecoveryEnvelope, household));   // the old holder puts its code again
        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        var recovery = _relay.RecoveryOf("alice").ShouldNotBeNull();
        recovery.Holder.ShouldBe(laptop.Worker.DeviceId);
        Recovery.Open(RecoveryCode.Key(RecoveryCode.Normalize(newer)!), household, new RecoveryReply(recovery.Body, recovery.Epoch, recovery.Holder))
            .ShouldNotBeNull();
        desktop.Worker.Store.RecoveryKey.ShouldBeNull();                              // a newer code exists: it forgets its own
        desktop.Worker.Store.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recovery_goes_up_only_sealed_at_the_households_current_epoch_waiting_for_this_pc_to_catch_up()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2");
        var study = await Start("Study PC");
        await WorkerPc.Pair(desktop, laptop);
        await WorkerPc.Pair(desktop, study);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        var household = desktop.Worker.Store.HouseholdId!;
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));
        var code = (await desktop.Next(NoticeKind.RecoveryCode)).RecoveryCode!;
        await laptop.Send<HouseholdReply>(new RemovePcRequest(9, study.Worker.DeviceId));
        await laptop.Worker.RunOnceAsync(CancellationToken.None);                     // the key moves to 2; the desktop hasn't heard
        _relay.Epoch(household).ShouldBe(2);

        desktop.Worker.Store.AddPending(new PendingOp(PendingOp.RecoveryEnvelope, household));
        await desktop.Worker.RunOnceAsync(CancellationToken.None);                    // sealed at 1: refused; it catches up and seals again
        desktop.Worker.Store.Epoch.ShouldBe(2);

        var recovery = _relay.RecoveryOf("alice").ShouldNotBeNull();
        recovery.Epoch.ShouldBe(2);
        Recovery.Open(RecoveryCode.Key(RecoveryCode.Normalize(code)!), household, new RecoveryReply(recovery.Body, 2, recovery.Holder))
            .ShouldNotBeNull().Key.ShouldBe(desktop.Worker.Store.CurrentKey);
        desktop.Worker.Store.Pending.ShouldNotContain(op => op.Kind == PendingOp.RecoveryEnvelope);
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
    public async Task A_pc_in_another_household_signing_in_as_an_account_linked_elsewhere_is_told_so_and_asks_nothing()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));                 // alice's account is linked to the desktop's
        var elsewhere = await Start("Study PC");
        var other = await Start("Other PC");
        await WorkerPc.Pair(elsewhere, other);                                      // the study PC is in a household of its own

        var reply = await elsewhere.Send<HouseholdReply>(SignIn(elsewhere, "alice"));

        reply.ShouldBe(new HouseholdReply(7, false, HouseholdWorker.LinkedElsewhere));
        _relay.Waiting(desktop.Worker.Store.HouseholdId!).ShouldBeEmpty();
        (elsewhere.Worker.Store.Session, elsewhere.Worker.Store.AskedToJoin).ShouldBe(((string?)null, (string?)null));
        elsewhere.Worker.Store.HouseholdId.ShouldNotBe(desktop.Worker.Store.HouseholdId);
    }

    [Fact]
    public async Task Signing_out_or_signing_in_as_another_account_forgets_this_pcs_recovery_key()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));
        desktop.Worker.Store.RecoveryKey.ShouldNotBeNull();

        await desktop.Send<HouseholdReply>(SignIn(desktop, "bob", salt: "b1"));
        desktop.Worker.Store.RecoveryKey.ShouldNotBeNull();                        // bob's own, made now
        var bobs = desktop.Worker.Store.RecoveryKey;
        (await desktop.Send<HouseholdReply>(new SignOutRequest(8))).Ok.ShouldBeTrue();

        desktop.Worker.Store.RecoveryKey.ShouldBeNull();
        desktop.Worker.Store.RecoveryCodeToShow.ShouldBeNull();
        bobs.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_recovery_the_server_no_longer_has_shows_as_missing_and_a_new_code_puts_it_right()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2");
        var study = await Start("Study PC");
        await WorkerPc.Pair(desktop, laptop);
        await WorkerPc.Pair(desktop, study);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        var household = desktop.Worker.Store.HouseholdId!;
        await desktop.Send<HouseholdReply>(SignIn(desktop, "alice"));             // the desktop makes the code and holds its key
        await laptop.Send<HouseholdReply>(SignIn(laptop, "alice", salt: "l1"));
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        laptop.Board.Household!.RecoveryMissing.ShouldBeFalse();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await laptop.Send<HouseholdReply>(new RemovePcRequest(9, desktop.Worker.DeviceId));
        await laptop.Worker.RunOnceAsync(CancellationToken.None);                  // removing the PC that holds the code takes the recovery away
        _relay.RecoveryOf("alice").ShouldBeNull();
        _clock.Advance(RelaySync.MembersEvery);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);

        laptop.Board.Household!.RecoveryMissing.ShouldBeTrue();
        (await laptop.Send<HouseholdReply>(new NewRecoveryCodeRequest(10))).ShouldBe(new HouseholdReply(10, true, "A new recovery code was made."));
        (await laptop.Next(NoticeKind.RecoveryCode)).RecoveryCode.ShouldNotBeNull();
        laptop.Board.Household!.RecoveryMissing.ShouldBeFalse();
        _relay.RecoveryOf("alice").ShouldNotBeNull().Epoch.ShouldBe(_relay.Epoch(household));
        _relay.RecoveryOf("alice")!.Epoch.ShouldBe(laptop.Worker.Store.Epoch);   // sealed at the key the server is at
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
