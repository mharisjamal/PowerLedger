using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>N2's approvals (households design §7): a member signed in asks its user about a PC waiting to join and seals the
/// key to it on Approve; a new key goes into the recovery envelope; signing out and deleting the account.</summary>
public sealed class ApprovalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeNetwork _network = new();
    private readonly FakeRelay _relay;
    private readonly List<WorkerPc> _pcs = [];

    public ApprovalTests() => _relay = new FakeRelay(_clock);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var pc in _pcs) await pc.DisposeAsync();
    }

    [Fact]
    public async Task A_member_signed_in_is_asked_about_a_pc_waiting_to_join_and_approving_lets_it_in()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));
        var household = desktop.Worker.Store.HouseholdId!;

        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        var prompt = await desktop.Next(NoticeKind.ApprovePrompt);
        (prompt.Text, prompt.FromName).ShouldBe(("A PC signed in as you asks to join your household. Approve it?", (string?)null));
        prompt.ComparisonCode.ShouldBe(HouseholdCrypto.ApprovalCode(PublicKeys(study).Sign, PublicKeys(study).Dh, PublicKeys(desktop).Dh));
        desktop.Board.Household!.PendingApprovals.ShouldBe(1);
        await study.Worker.RunOnceAsync(CancellationToken.None);                  // not yet
        study.Worker.Store.HouseholdId.ShouldBeNull();

        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(9, prompt.PromptId!, true));
        (await desktop.Next(NoticeKind.Info, text => text.Contains("now in your household"))).ShouldNotBeNull();
        await desktop.Worker.Running;
        _relay.Members(household).Keys.ShouldContain(study.Worker.DeviceId);
        _relay.Waiting(household).ShouldBeEmpty();
        desktop.Board.Household!.PendingApprovals.ShouldBe(0);

        await study.Worker.RunOnceAsync(CancellationToken.None);
        var check = await study.Next(NoticeKind.ConfirmJoin);                     // the same code, worked out on the study PC
        (check.Text, check.ComparisonCode).ShouldBe(($"Did the PC that approved this one show {prompt.ComparisonCode}?", prompt.ComparisonCode));
        study.Worker.Store.HouseholdId.ShouldBeNull();                            // nothing until its user says so
        await study.Send<HouseholdReply>(new AnswerPromptRequest(10, check.PromptId!, true));
        await study.Worker.Running;
        study.Worker.Store.HouseholdId.ShouldBe(household);
        desktop.Household.Member(study.Worker.DeviceId).ShouldNotBeNull().Name.ShouldBe(HouseholdWorker.NewPcName);   // known by its keys
        study.Household.Upsert([new HouseholdRow(study.Worker.DeviceId, Now.AddHours(-2).ToUnixTimeMilliseconds(), 7, 1, 1, 1, 1, 0, 0,
            3600, 0, 0, 3600, 0, 0, 1_000, "GBP", Now.ToUnixTimeMilliseconds() + 1)]);
        await study.Worker.RunOnceAsync(CancellationToken.None);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Household.Row(study.Worker.DeviceId, Now.AddHours(-2).ToUnixTimeMilliseconds()).ShouldNotBeNull().EnergyWh.ShouldBe(7);
        desktop.Household.Member(study.Worker.DeviceId)!.Name.ShouldBe("Study PC");
        study.Worker.Store.CurrentKey.ShouldBe(desktop.Worker.Store.CurrentKey);
        (await study.Next(NoticeKind.Info, text => text == "This PC joined your household.")).ShouldNotBeNull();
        study.Worker.Store.AskedToJoin.ShouldBeNull();
    }

    [Fact]
    public async Task An_approved_pc_whose_user_sees_another_code_doesnt_join_and_takes_itself_off()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));
        var household = desktop.Worker.Store.HouseholdId!;
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(9, (await desktop.Next(NoticeKind.ApprovePrompt)).PromptId!, true));
        await desktop.Worker.Running;

        await study.Worker.RunOnceAsync(CancellationToken.None);
        await study.Send<HouseholdReply>(new AnswerPromptRequest(10, (await study.Next(NoticeKind.ConfirmJoin)).PromptId!, false));
        await study.Worker.Running;
        await study.Worker.RunOnceAsync(CancellationToken.None);

        study.Worker.Store.HouseholdId.ShouldBeNull();
        study.Worker.Store.AskedToJoin.ShouldBeNull();
        _relay.Members(household)[study.Worker.DeviceId].Removed.ShouldNotBeNull();
        (await study.Next(NoticeKind.Info, text => text.StartsWith("This PC didn't join", StringComparison.Ordinal))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Not_approving_turns_it_away_on_the_server()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        var prompt = await desktop.Next(NoticeKind.ApprovePrompt);

        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(9, prompt.PromptId!, false));
        await desktop.Worker.Running;
        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        _relay.Posted($"POST /v1/households/{desktop.Worker.Store.HouseholdId}/requests").ShouldBe(0);
        _relay.Posted($"DELETE /v1/households/{desktop.Worker.Store.HouseholdId}/requests/{study.Worker.DeviceId}").ShouldBe(1);
        _relay.Waiting(desktop.Worker.Store.HouseholdId!).ShouldBeEmpty();
        desktop.Board.Household!.PendingApprovals.ShouldBe(0);
        desktop.Worker.Prompts.Open.ShouldBe(0);
    }

    [Fact]
    public async Task A_pc_signed_in_as_another_account_is_asked_about_without_saying_it_is_you()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        using var stranger = DeviceKeys.Create();
        _relay.Ask(desktop.Worker.Store.HouseholdId!, stranger, "another-account");

        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        (await desktop.Next(NoticeKind.ApprovePrompt)).Text.ShouldBe("A PC asks to join your household. Approve it?");
    }

    [Fact]
    public async Task An_approval_refused_for_an_older_key_goes_again_with_the_newest()
    {
        var (desktop, laptop) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var household = desktop.Worker.Store.HouseholdId!;
        var newer = HouseholdCrypto.NewKey();                                     // the laptop rotated; the desktop hasn't heard
        using var laptopKeys = laptop.Worker.Store.DeviceKeys();
        using var client = new RelayClient(FakeRelay.Endpoint, _clock, _relay);
        await client.PostKeysAsync(laptopKeys, household, 2,
            KeyWrap.For(laptopKeys, household, 2, newer, [desktop.Household.Member(desktop.Worker.DeviceId)!, laptop.Household.Member(laptop.Worker.DeviceId)!]),
            CancellationToken.None);
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(9, (await desktop.Next(NoticeKind.ApprovePrompt)).PromptId!, true));
        await desktop.Worker.Running;

        desktop.Worker.Store.Epoch.ShouldBe(2);                                   // it caught up
        _relay.Waiting(household).ShouldBe([study.Worker.DeviceId]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);                // and approves again with the newest key
        _relay.Waiting(household).ShouldBeEmpty();
        await study.Worker.RunOnceAsync(CancellationToken.None);
        await study.Send<HouseholdReply>(new AnswerPromptRequest(10, (await study.Next(NoticeKind.ConfirmJoin)).PromptId!, true));
        await study.Worker.Running;
        study.Worker.Store.Epoch.ShouldBe(2);
        study.Worker.Store.CurrentKey.ShouldBe(newer);
    }

    [Fact]
    public async Task A_link_the_server_lost_is_made_again_at_the_next_turn_after_a_while()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var household = desktop.Worker.Store.HouseholdId!;
        _relay.Unlink("alice");

        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        _relay.LinkOf("alice").ShouldBeNull();                                    // linked at sign-in: not looked at again yet
        _clock.Advance(RelaySync.MembersEvery);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        _relay.LinkOf("alice").ShouldBe(household);
    }

    [Fact]
    public async Task A_new_key_goes_into_the_recovery_envelope_when_this_pc_is_signed_in()
    {
        var (desktop, laptop) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var code = (await desktop.Next(NoticeKind.RecoveryCode)).RecoveryCode!;
        var household = desktop.Worker.Store.HouseholdId!;

        await desktop.Send<HouseholdReply>(new RemovePcRequest(3, laptop.Worker.DeviceId));
        desktop.Worker.Store.Pending.Select(op => op.Kind).ShouldNotContain(PendingOp.RecoveryEnvelope);   // not before the server takes the key
        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        var envelope = _relay.RecoveryOf("alice").ShouldNotBeNull();
        envelope.Epoch.ShouldBe(2);
        Recovery.Open(RecoveryCode.Key(RecoveryCode.Normalize(code)!), new RecoveryReply(household, 2, envelope.Body))
            .ShouldBe(desktop.Worker.Store.CurrentKey);
        desktop.Worker.Store.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task Signing_out_and_deleting_the_account_go_to_the_server_and_the_household_carries_on()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        _relay.Sessions.ShouldBe(1);

        (await desktop.Send<HouseholdReply>(new SignOutRequest(4))).ShouldBe(new HouseholdReply(4, true, "Signed out."));
        _relay.Sessions.ShouldBe(0);
        desktop.Worker.Store.Session.ShouldBeNull();
        desktop.Board.Household!.SignedIn.ShouldBeFalse();

        await desktop.Send<HouseholdReply>(SignIn(desktop, salt: "again"));
        (await desktop.Send<HouseholdReply>(new DeleteAccountRequest(5))).ShouldBe(
            new HouseholdReply(5, true, "Your account was deleted. Your household carries on without sign-in."));
        (_relay.LinkOf("alice"), _relay.RecoveryOf("alice"), _relay.Sessions).ShouldBe(((string?)null, null, 0));
        (desktop.Worker.Store.Session, desktop.Worker.Store.RecoveryKey).ShouldBe((null, null));
        desktop.Worker.Store.HouseholdId.ShouldNotBeNull();
    }

    private static (byte[] Sign, byte[] Dh) PublicKeys(WorkerPc pc)
    {
        using var keys = pc.Worker.Store.DeviceKeys();
        return (keys.SignPublic, keys.DhPublic);
    }

    private static SignInRequest SignIn(WorkerPc pc, string salt = "salt-1") =>
        new(7, "google", FakeRelay.IdToken("google", "alice", pc.Worker.DeviceId, salt), salt);

    private async Task<WorkerPc> Start(string name, ChassisKind kind = ChassisKind.Laptop)
    {
        var pc = new WorkerPc(name, kind, _network, _relay, _clock, appAtTheScreen: true);
        _pcs.Add(pc);
        await pc.Worker.StartAsync(CancellationToken.None);
        return pc;
    }

    private async Task<(WorkerPc Desktop, WorkerPc Laptop)> Household()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2");
        await WorkerPc.Pair(desktop, laptop);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        return (desktop, laptop);
    }
}
