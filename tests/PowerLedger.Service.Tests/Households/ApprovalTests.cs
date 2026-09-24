using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>N2's approvals (households design §7, plan 0.9): commit, then reveal, so both screens show the approval code
/// before anything is sealed; the waiting PC enters only with its user's word and the committed approver's envelope;
/// unanswered prompts come back; a request turned away lets the user ask again; signing out and deleting the account.</summary>
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
    public async Task Both_screens_show_the_approval_code_before_anything_is_sealed_and_the_pc_enters_once_both_users_said_so()
    {
        var (desktop, laptop) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));
        var household = desktop.Worker.Store.HouseholdId!;

        await desktop.Worker.RunOnceAsync(CancellationToken.None);                  // commits to a nonce
        _relay.RequestOf(household, study.Worker.DeviceId).ShouldNotBeNull().ShouldSatisfyAllConditions(
            request => request.Approver.ShouldBe(desktop.Worker.DeviceId), request => request.Nonce.ShouldBeNull());
        await study.Worker.RunOnceAsync(CancellationToken.None);                    // answers with its own
        _relay.RequestOf(household, study.Worker.DeviceId)!.Nonce.ShouldNotBeNull();
        await desktop.Worker.RunOnceAsync(CancellationToken.None);                  // reveals, and asks its user
        var approve = await desktop.Next(NoticeKind.ApprovePrompt);
        await study.Worker.RunOnceAsync(CancellationToken.None);                    // checks the reveal, and asks its user at once
        var confirm = await study.Next(NoticeKind.ConfirmJoin);

        var request = _relay.RequestOf(household, study.Worker.DeviceId)!;
        var code = HouseholdCrypto.ApprovalCode(PublicKeys(study).Sign, PublicKeys(study).Dh, PublicKeys(desktop).Sign, PublicKeys(desktop).Dh,
            Wire.Decode(request.Nonce)!, Wire.Decode(request.Reveal)!);
        (approve.ComparisonCode, confirm.ComparisonCode).ShouldBe((code, code));
        approve.Text.ShouldBe("A PC signed in as you asks to join your household. Approve it?");
        confirm.Text.ShouldBe($"Does your other PC show {code}? Approve it there too.");
        _relay.Members(household).Keys.ShouldNotContain(study.Worker.DeviceId);    // nothing sealed while both screens show it
        _relay.Sealed(household, 1).ShouldNotContain(study.Worker.DeviceId);
        desktop.Board.Household!.PendingApprovals.ShouldBe(1);

        await study.Send<HouseholdReply>(new AnswerPromptRequest(9, confirm.PromptId!, true));
        await study.Worker.Running;
        await study.Worker.RunOnceAsync(CancellationToken.None);
        study.Worker.Store.HouseholdId.ShouldBeNull();                               // not before the approval is there
        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(10, approve.PromptId!, true));
        (await desktop.Next(NoticeKind.Info, text => text.Contains("now in your household", StringComparison.Ordinal))).ShouldNotBeNull();
        await desktop.Worker.Running;
        desktop.Board.Household!.PendingApprovals.ShouldBe(0);
        desktop.Household.Member(study.Worker.DeviceId).ShouldNotBeNull().Name.ShouldBe(HouseholdWorker.NewPcName);   // known by its keys
        await study.Worker.RunOnceAsync(CancellationToken.None);

        study.Worker.Store.HouseholdId.ShouldBe(household);
        study.Worker.Store.CurrentKey.ShouldBe(desktop.Worker.Store.CurrentKey);
        (await study.Next(NoticeKind.Info, text => text == "This PC joined your household.")).ShouldNotBeNull();
        _relay.RequestOf(household, study.Worker.DeviceId).ShouldBeNull();          // taken off the server once it is in
        study.Household.Member(laptop.Worker.DeviceId).ShouldNotBeNull().LeftMs.ShouldBeNull();   // known from the approval, before any batch
        study.Household.Upsert([new HouseholdRow(study.Worker.DeviceId, Now.AddHours(-2).ToUnixTimeMilliseconds(), 7, 1, 1, 1, 1, 0, 0,
            3600, 0, 0, 3600, 0, 0, 1_000, "GBP", Now.ToUnixTimeMilliseconds() + 1)]);
        await study.Worker.RunOnceAsync(CancellationToken.None);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Household.Row(study.Worker.DeviceId, Now.AddHours(-2).ToUnixTimeMilliseconds()).ShouldNotBeNull().EnergyWh.ShouldBe(7);
        desktop.Household.Member(study.Worker.DeviceId)!.Name.ShouldBe("Study PC");
    }

    [Fact]
    public async Task They_dont_match_takes_the_request_off_the_server_before_the_approval_so_nothing_is_sealed()
    {
        var (desktop, study, household) = await BothAsked();
        var (approve, confirm) = (await desktop.Next(NoticeKind.ApprovePrompt), await study.Next(NoticeKind.ConfirmJoin));

        await study.Send<HouseholdReply>(new AnswerPromptRequest(9, confirm.PromptId!, false));
        await study.Worker.Running;

        _relay.RequestOf(household, study.Worker.DeviceId).ShouldBeNull();
        study.Worker.Store.AskedToJoin.ShouldBeNull();
        study.Board.Household!.CanAskAgain.ShouldBeTrue();
        (await study.Next(NoticeKind.Info, text => text.StartsWith("This PC didn't join", StringComparison.Ordinal))).ShouldNotBeNull();
        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(10, approve.PromptId!, true));
        await desktop.Worker.Running;
        _relay.Members(household).Keys.ShouldNotContain(study.Worker.DeviceId);
        desktop.Household.Member(study.Worker.DeviceId).ShouldBeNull();
    }

    [Fact]
    public async Task They_dont_match_after_the_approval_takes_the_pc_out_of_the_household_on_the_server()
    {
        var (desktop, study, household) = await BothAsked();
        var (approve, confirm) = (await desktop.Next(NoticeKind.ApprovePrompt), await study.Next(NoticeKind.ConfirmJoin));
        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(9, approve.PromptId!, true));
        await desktop.Worker.Running;
        _relay.Members(household)[study.Worker.DeviceId].Removed.ShouldBeNull();

        await study.Send<HouseholdReply>(new AnswerPromptRequest(10, confirm.PromptId!, false));
        await study.Worker.Running;
        await study.Worker.RunOnceAsync(CancellationToken.None);

        _relay.Members(household)[study.Worker.DeviceId].Removed.ShouldNotBeNull();
        study.Worker.Store.HouseholdId.ShouldBeNull();
        study.Worker.Store.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unanswered_prompt_comes_back_at_the_next_turn_and_never_counts_as_a_no()
    {
        var (desktop, study, household) = await BothAsked();
        var (approve, confirm) = (await desktop.Next(NoticeKind.ApprovePrompt), await study.Next(NoticeKind.ConfirmJoin));

        _clock.Advance(HouseholdPrompts.Timeout);
        await desktop.Worker.Running;
        await study.Worker.Running;

        _relay.RequestOf(household, study.Worker.DeviceId).ShouldNotBeNull();       // not turned away
        study.Worker.Store.AskedToJoin.ShouldBe(household);                           // nor did it give up
        study.Worker.Store.Pending.ShouldBeEmpty();
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        (await desktop.Next(NoticeKind.ApprovePrompt)).ComparisonCode.ShouldBe(approve.ComparisonCode);
        await study.Worker.RunOnceAsync(CancellationToken.None);
        (await study.Next(NoticeKind.ConfirmJoin)).ComparisonCode.ShouldBe(confirm.ComparisonCode);
    }

    [Fact]
    public async Task An_approved_pc_enters_only_with_the_envelope_its_approver_sealed_for_the_epoch_the_server_names()
    {
        var (desktop, study, household) = await BothAsked();
        var (approve, confirm) = (await desktop.Next(NoticeKind.ApprovePrompt), await study.Next(NoticeKind.ConfirmJoin));
        await study.Send<HouseholdReply>(new AnswerPromptRequest(9, confirm.PromptId!, true));
        await study.Worker.Running;
        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(10, approve.PromptId!, true));
        await desktop.Worker.Running;
        using var other = DeviceKeys.Create();                                       // the server hands over a key sealed by another PC
        var planted = Wire.Encode(HouseholdCrypto.WrapFor(other.Dh, PublicKeys(study).Dh, HouseholdCrypto.NewKey(), Approval.Context(household, 1)));
        _relay.Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/keys/1", StringComparison.Ordinal)
            && request.Headers.GetValues("X-PL-Device").Single() == study.Worker.DeviceId
            ? FakeRelay.Json(new System.Text.Json.Nodes.JsonObject { ["epoch"] = 1, ["from"] = other.DeviceId, ["body"] = planted })
            : null;

        await study.Worker.RunOnceAsync(CancellationToken.None);

        study.Worker.Store.HouseholdId.ShouldBeNull();
        (await study.Next(NoticeKind.Info, text => text.StartsWith("This PC didn't join", StringComparison.Ordinal))).ShouldNotBeNull();
        _relay.Intercept = null;
        await study.Worker.RunOnceAsync(CancellationToken.None);
        _relay.Members(household)[study.Worker.DeviceId].Removed.ShouldNotBeNull();  // it took itself out
    }

    [Fact]
    public async Task A_member_runs_one_approval_at_a_time_and_starts_at_most_five_a_day()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var household = desktop.Worker.Store.HouseholdId!;
        _relay.Link("another-account", household);
        var waiting = Enumerable.Range(0, 7).Select(_ => DeviceKeys.Create()).ToList();
        foreach (var pc in waiting)
        {
            _relay.Ask(household, pc, "another-account");
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        for (var started = 0; started < HouseholdWorker.ApprovalsADay; started++)
        {
            await desktop.Worker.RunOnceAsync(CancellationToken.None);
            var committed = waiting.Where(pc => _relay.RequestOf(household, pc.DeviceId)?.Approver == desktop.Worker.DeviceId).ToList();
            committed.ShouldHaveSingleItem();                                         // one at a time
            _relay.Deny(household, committed[0].DeviceId);                            // another member turned it away
        }
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        waiting.ShouldAllBe(pc => _relay.RequestOf(household, pc.DeviceId) == null || _relay.RequestOf(household, pc.DeviceId)!.Approver == null);

        _clock.Advance(TimeSpan.FromDays(1));                                         // the next day, with the two left asking again
        foreach (var pc in waiting.Where(pc => _relay.RequestOf(household, pc.DeviceId) is not null)) _relay.Ask(household, pc, "another-account");
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        waiting.Count(pc => _relay.RequestOf(household, pc.DeviceId)?.Approver == desktop.Worker.DeviceId).ShouldBe(1);
        foreach (var pc in waiting) pc.Dispose();
    }

    [Fact]
    public async Task A_request_turned_away_lets_the_user_ask_again_and_the_pc_never_asks_by_itself()
    {
        var (desktop, study, household) = await BothAsked();
        var approve = await desktop.Next(NoticeKind.ApprovePrompt);

        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(9, approve.PromptId!, false));
        await desktop.Worker.Running;
        _relay.RequestOf(household, study.Worker.DeviceId).ShouldBeNull();
        await study.Worker.RunOnceAsync(CancellationToken.None);
        await study.Worker.RunOnceAsync(CancellationToken.None);

        study.Board.Household!.CanAskAgain.ShouldBeTrue();
        study.Worker.Store.AskedToJoin.ShouldBeNull();
        _relay.RequestOf(household, study.Worker.DeviceId).ShouldBeNull();           // never by itself
        (await study.Send<HouseholdReply>(new AskAgainRequest(11))).ShouldBe(new HouseholdReply(11, true, HouseholdWorker.WaitingForApproval));
        _relay.RequestOf(household, study.Worker.DeviceId).ShouldNotBeNull();
        study.Worker.Store.AskedToJoin.ShouldBe(household);
        study.Board.Household!.CanAskAgain.ShouldBeFalse();
        (await desktop.Send<HouseholdReply>(new AskAgainRequest(12))).ShouldBe(new HouseholdReply(12, false, HouseholdWorker.AskedAgainButIn));
    }

    [Fact]
    public async Task A_request_that_lapses_unanswered_lets_the_user_ask_again()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));

        _clock.Advance(TimeSpan.FromHours(25));
        await study.Worker.RunOnceAsync(CancellationToken.None);

        study.Board.Household!.CanAskAgain.ShouldBeTrue();
        (await study.Next(NoticeKind.Info, text => text.Contains("You can ask again", StringComparison.Ordinal))).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_pc_signed_in_as_another_account_is_asked_about_without_saying_it_is_you()
    {
        var (desktop, _) = await Household();
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var household = desktop.Worker.Store.HouseholdId!;
        _relay.Link("another-account", household);
        using var stranger = DeviceKeys.Create();
        _relay.Ask(household, stranger, "another-account");
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        _relay.Answer(household, stranger.DeviceId, HouseholdCrypto.NewNonce());

        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        (await desktop.Next(NoticeKind.ApprovePrompt)).Text.ShouldBe("A PC asks to join your household. Approve it?");
    }

    [Fact]
    public async Task An_approval_the_server_refuses_because_the_key_moved_on_goes_again_with_the_newest()
    {
        var (desktop, laptop) = await Household();
        var (_, study, household) = await BothAsked(desktop);
        var (approve, confirm) = (await desktop.Next(NoticeKind.ApprovePrompt), await study.Next(NoticeKind.ConfirmJoin));
        await study.Send<HouseholdReply>(new AnswerPromptRequest(9, confirm.PromptId!, true));
        await study.Worker.Running;                                                // its user's answer is kept before its next turn
        var newer = HouseholdCrypto.NewKey();                                      // the laptop rotated; the desktop hasn't heard
        using var laptopKeys = laptop.Worker.Store.DeviceKeys();
        using var client = new RelayClient(FakeRelay.Endpoint, _clock, _relay);
        (await client.PostKeysAsync(laptopKeys, household, 2,
            KeyWrap.For(laptopKeys, household, 2, newer, [desktop.Household.Member(desktop.Worker.DeviceId)!, laptop.Household.Member(laptop.Worker.DeviceId)!]),
            CancellationToken.None)).Ok.ShouldBeTrue();

        await desktop.Send<HouseholdReply>(new AnswerPromptRequest(10, approve.PromptId!, true));
        await desktop.Worker.Running;
        desktop.Worker.Store.Epoch.ShouldBe(2);                                    // it caught up
        _relay.Members(household).Keys.ShouldNotContain(study.Worker.DeviceId);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);                 // and approves again with the newest key, not asking twice
        desktop.Drain().ShouldNotContain(notice => notice.Kind == NoticeKind.ApprovePrompt);
        await study.Worker.RunOnceAsync(CancellationToken.None);

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

    /// <summary>A household with a member signed in, and a study PC signed in as the same account asking to join, both asked
    /// about the approval: its code on both screens, nothing sealed yet.</summary>
    private async Task<(WorkerPc Desktop, WorkerPc Study, string Household)> BothAsked(WorkerPc? member = null)
    {
        var desktop = member ?? (await Household()).Desktop;
        await desktop.Send<HouseholdReply>(SignIn(desktop));
        var study = await Start("Study PC", ChassisKind.Desktop);
        await study.Send<HouseholdReply>(SignIn(study));
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await study.Worker.RunOnceAsync(CancellationToken.None);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await study.Worker.RunOnceAsync(CancellationToken.None);
        return (desktop, study, desktop.Worker.Store.HouseholdId!);
    }
}
