using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The household worker's requests (plan 0.2), pairing on loopback with discovery faked, pairing by code, removing
/// and leaving with the key's rotation, and what the status carries.</summary>
public sealed class HouseholdWorkerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeNetwork _network = new();
    private readonly FakeRelay _relay;
    private readonly List<WorkerPc> _pcs = [];

    public HouseholdWorkerTests() => _relay = new FakeRelay(_clock);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var pc in _pcs) await pc.DisposeAsync();
    }

    [Fact]
    public async Task Browsing_lists_the_other_pcs_on_the_network_but_not_this_one()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);

        var found = (await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(1))).Pcs;

        found.ShouldBe([new FoundPc(laptop.Worker.InstanceId, "Laptop-2", InThisHousehold: false)]);
    }

    [Fact]
    public async Task Adding_a_pc_on_the_network_pairs_them_once_its_user_presses_join()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(1));

        (await desktop.Send<HouseholdReply>(new AddPcRequest(2, laptop.Worker.InstanceId))).ShouldBe(new HouseholdReply(2, true, "Connecting to Laptop-2."));

        var confirm = await desktop.Next(NoticeKind.ConfirmCode);
        confirm.Text.ShouldBe($"Does Laptop-2 show {confirm.ComparisonCode}?");
        confirm.FromName.ShouldBe("Laptop-2");
        var prompt = await laptop.Next(NoticeKind.JoinPrompt);
        prompt.ComparisonCode.ShouldBe(confirm.ComparisonCode);
        prompt.FromName.ShouldBe("Desktop-7");
        (await laptop.Send<HouseholdReply>(new AnswerPromptRequest(3, prompt.PromptId!, true))).Ok.ShouldBeTrue();
        (await desktop.Send<HouseholdReply>(new AnswerPromptRequest(4, confirm.PromptId!, true))).Ok.ShouldBeTrue();

        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 joined your household.");
        (await laptop.Next(NoticeKind.Info)).Text.ShouldBe("This PC joined Desktop-7's household.");
        var household = desktop.Worker.Store.HouseholdId.ShouldNotBeNull();
        laptop.Worker.Store.HouseholdId.ShouldBe(household);
        laptop.Worker.Store.CurrentKey.ShouldBe(desktop.Worker.Store.CurrentKey);
        desktop.Worker.Store.Pending.Select(op => op.Kind).ShouldBe([PendingOp.Create, PendingOp.Add]);

        var status = desktop.Board.Household.ShouldNotBeNull();
        status.HouseholdId.ShouldBe(household);
        status.Members.Select(member => (member.Name, member.Kind, member.IsThisPc, member.Left))
            .ShouldBe([("Desktop-7", ChassisKind.Desktop, true, false), ("Laptop-2", ChassisKind.Laptop, false, false)], ignoreOrder: true);
        laptop.Board.Household.ShouldNotBeNull().Members.Count.ShouldBe(2);
        (await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(4))).Pcs.ShouldHaveSingleItem().InThisHousehold.ShouldBeTrue();

        await desktop.Worker.RunOnceAsync(CancellationToken.None);               // the server hears of it
        desktop.Worker.Store.Pending.ShouldBeEmpty();
        _relay.Members(household).Keys.ShouldBe([desktop.Worker.DeviceId, laptop.Worker.DeviceId], ignoreOrder: true);
    }

    [Fact]
    public async Task With_nobody_at_the_other_screen_the_pairing_is_refused_at_once()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop, appAtTheScreen: false);
        await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(1));

        await desktop.Send<HouseholdReply>(new AddPcRequest(2, laptop.Worker.InstanceId));

        var confirm = await desktop.Next(NoticeKind.ConfirmCode);
        (await desktop.Next(NoticeKind.Withdraw)).PromptId.ShouldBe(confirm.PromptId);   // the laptop said no: the question closes
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 didn't join.");
        (desktop.Worker.Store.HouseholdId, laptop.Worker.Store.HouseholdId).ShouldBe((null, null));
    }

    [Fact]
    public async Task Adding_a_pc_not_found_or_already_in_the_household_is_refused()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);

        (await desktop.Send<HouseholdReply>(new AddPcRequest(1, "nobody"))).ShouldBe(
            new HouseholdReply(1, false, "That PC isn't on the network any more. Look again."));
    }

    [Fact]
    public async Task Pairing_by_code_goes_through_the_server_and_asks_without_a_comparison_code()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);

        var started = await desktop.Send<HouseholdReply>(new StartCodePairingRequest(1));
        started.Ok.ShouldBeTrue();
        started.Message.ShouldBe("Type this code on the other PC within 10 minutes.");
        var code = started.Code.ShouldNotBeNull();
        (await laptop.Send<HouseholdReply>(new JoinByCodeRequest(2, "no"))).Ok.ShouldBeFalse();
        (await laptop.Send<HouseholdReply>(new JoinByCodeRequest(3, code))).ShouldBe(new HouseholdReply(3, true, "Looking for the PC that made that code."));

        var prompt = await laptop.Next(NoticeKind.JoinPrompt);
        (prompt.Text, prompt.FromName, prompt.ComparisonCode).ShouldBe(("Join the household of the PC that made this code?", (string?)null, (string?)null));
        await laptop.Send<HouseholdReply>(new AnswerPromptRequest(4, prompt.PromptId!, true));

        (await laptop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("This PC joined Desktop-7's household.");
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 joined your household.");
        laptop.Worker.Store.HouseholdId.ShouldBe(desktop.Worker.Store.HouseholdId);
    }

    [Fact]
    public async Task Joining_by_code_on_a_pc_that_made_a_code_stops_that_code_first()
    {
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);

        var made = await laptop.Send<HouseholdReply>(new StartCodePairingRequest(1));   // the tab was opened
        made.Ok.ShouldBeTrue();
        var join = await laptop.Send<HouseholdReply>(new JoinByCodeRequest(2, "K7QM-2XHD-9PW4-R8TA"));   // a code typed on the same tab

        join.ShouldBe(new HouseholdReply(2, true, "Looking for the PC that made that code."));
        string[] outcomes = [(await laptop.Next(NoticeKind.PairingProgress)).Text, (await laptop.Next(NoticeKind.PairingProgress)).Text];
        outcomes.ShouldBe(
            ["Adding the other PC was cancelled.", "No PC is waiting with that code. Check it, or make a new one on the other PC."], ignoreOrder: true);
    }

    [Fact]
    public async Task Cancelling_stops_a_code_and_frees_pairing_for_another()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        (await desktop.Send<HouseholdReply>(new CancelPairingRequest(1))).ShouldBe(new HouseholdReply(1, true, "No pairing is under way."));
        (await desktop.Send<HouseholdReply>(new StartCodePairingRequest(2))).Ok.ShouldBeTrue();
        (await desktop.Send<HouseholdReply>(new StartCodePairingRequest(3))).Message.ShouldBe(PairingGate.Busy);

        (await desktop.Send<HouseholdReply>(new CancelPairingRequest(4))).ShouldBe(new HouseholdReply(4, true, "Pairing stopped."));

        (await desktop.Send<HouseholdReply>(new StartCodePairingRequest(5))).Ok.ShouldBeTrue();
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Adding the other PC was cancelled.");
    }

    [Fact]
    public async Task Cancelling_on_the_joining_pc_closes_its_question_and_tells_the_adding_pc()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(1));
        await desktop.Send<HouseholdReply>(new AddPcRequest(2, laptop.Worker.InstanceId));
        var prompt = await laptop.Next(NoticeKind.JoinPrompt);
        var confirm = await desktop.Next(NoticeKind.ConfirmCode);

        (await laptop.Send<HouseholdReply>(new CancelPairingRequest(3))).ShouldBe(new HouseholdReply(3, true, "Pairing stopped."));

        (await laptop.Next(NoticeKind.Withdraw)).PromptId.ShouldBe(prompt.PromptId);
        (await desktop.Next(NoticeKind.Withdraw)).PromptId.ShouldBe(confirm.PromptId);
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 stopped the pairing.");
        (desktop.Worker.Store.HouseholdId, laptop.Worker.Store.HouseholdId).ShouldBe((null, null));
    }

    [Fact]
    public async Task This_pcs_own_adds_that_the_other_pc_refuses_dont_pause_pairing_here()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop, appAtTheScreen: false);    // says no at once
        for (var i = 0; i < PairingGate.MaxRefusals; i++)
        {
            await desktop.Send<FoundPcsReply>(new BrowsePcsRequest(1));
            (await desktop.Send<HouseholdReply>(new AddPcRequest(2, laptop.Worker.InstanceId))).Ok.ShouldBeTrue();
            (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 didn't join.");
            await desktop.Worker.Running;
        }

        (await desktop.Send<HouseholdReply>(new StartCodePairingRequest(3))).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task An_address_whose_pairings_keep_coming_to_nothing_goes_unanswered_and_only_a_few_notices_reach_the_app()
    {
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        async Task<byte[]?> TryPair(bool good)
        {
            await using var channel = await LanConnector.ConnectAsync(IPAddress.Loopback, laptop.Worker.Port, TimeSpan.FromSeconds(5));
            using var keys = DeviceKeys.Create();
            var hello = LanMessages.Hello("pair", keys.DhPublic, keys, "Stranger", ChassisKind.Laptop, "c3");
            await channel.SendAsync(LanMessages.Write(good ? hello : hello with { Sign = "not a key" }));
            return await channel.ReceiveAsync();
        }

        for (var i = 0; i < PairingGate.MaxRefusals; i++) (await TryPair(good: false)).ShouldBeNull();   // a hello that isn't a good one

        (await TryPair(good: true)).ShouldBeNull();                               // not even this PC's hello back
        var told = laptop.Drain();
        told.Count(notice => notice.Kind == NoticeKind.Info).ShouldBe(StrangerGate.MaxNotices);
        told.ShouldNotContain(notice => notice.Kind == NoticeKind.JoinPrompt);
    }

    [Fact]
    public async Task Removing_a_pc_changes_the_key_for_those_who_stay_and_the_removed_pc_cant_read_what_comes_after()
    {
        var (desktop, laptop, study) = await Household();
        desktop.Worker.Store.HistoryPosted = [desktop.Worker.DeviceId, laptop.Worker.DeviceId, study.Worker.DeviceId];
        foreach (var pc in new[] { desktop, laptop, study }) await pc.Worker.RunOnceAsync(CancellationToken.None);
        var oldKey = study.Worker.Store.CurrentKey.ShouldNotBeNull();
        _clock.Advance(TimeSpan.FromMinutes(1));

        (await desktop.Send<HouseholdReply>(new RemovePcRequest(1, study.Worker.DeviceId))).ShouldBe(
            new HouseholdReply(1, true, "Study PC was removed from the household."));

        desktop.Worker.Store.Pending.Select(op => op.Kind).ShouldBe([PendingOp.Remove, PendingOp.Keys]);
        desktop.Worker.Store.RotationKey.ShouldNotBeNull().Epoch.ShouldBe(2);
        desktop.Board.Household!.Members.ShouldAllBe(member => member.DeviceId != study.Worker.DeviceId || member.Left);   // listed only while it has rows
        _relay.Down = true;
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Worker.Store.Epoch.ShouldBe(1);                                  // not before the server takes the new key
        _relay.Down = false;

        desktop.Household.Upsert([Row(desktop.Worker.DeviceId, 1, 42, changed: Now.ToUnixTimeMilliseconds() + 1)]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Worker.Store.Epoch.ShouldBe(2);
        desktop.Worker.Store.RotationKey.ShouldBeNull();
        _relay.Sealed(desktop.Worker.Store.HouseholdId!, 2).ShouldBe([desktop.Worker.DeviceId, laptop.Worker.DeviceId], ignoreOrder: true);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        await study.Worker.RunOnceAsync(CancellationToken.None);

        laptop.Worker.Store.Epoch.ShouldBeGreaterThanOrEqualTo(2);                // it heard of the removal too, and may have made a key of its own
        laptop.Household.Row(desktop.Worker.DeviceId, Hour(1)).ShouldNotBeNull().EnergyWh.ShouldBe(42);
        study.Worker.Store.HouseholdId.ShouldBeNull();
        (await study.Next(NoticeKind.Info, text => text != "This PC joined Desktop-7's household.")).Text.ShouldBe("This PC was removed from the household.");
        study.Household.Row(desktop.Worker.DeviceId, Hour(1)).ShouldBeNull();
        var after = _relay.Batches.Last(batch => batch.Device == desktop.Worker.DeviceId);
        after.Epoch.ShouldBe(2);
        Should.Throw<System.Security.Cryptography.CryptographicException>(() => HouseholdCrypto.Open(
            oldKey, after.Body, HouseholdCrypto.BatchAad(desktop.Worker.Store.HouseholdId!, desktop.Worker.DeviceId, after.Epoch, after.Seq)));
    }

    [Fact]
    public async Task Leaving_takes_only_this_pc_off_the_server_and_a_member_that_stays_makes_the_new_key()
    {
        var (desktop, laptop, study) = await Household();
        var household = laptop.Worker.Store.HouseholdId!;
        var oldKey = laptop.Worker.Store.CurrentKey;

        (await laptop.Send<HouseholdReply>(new LeaveHouseholdRequest(1))).ShouldBe(new HouseholdReply(1, true, "This PC left the household."));

        laptop.Worker.Store.HouseholdId.ShouldBeNull();
        laptop.Board.Household.ShouldNotBeNull().Members.ShouldBeEmpty();
        laptop.Worker.Store.Pending.Select(op => op.Kind).ShouldBe([PendingOp.Remove]);  // no key of its own for those who stay
        (await laptop.Send<HouseholdReply>(new LeaveHouseholdRequest(2))).ShouldBe(new HouseholdReply(2, false, HouseholdWorker.NotInOne));

        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        laptop.Worker.Store.Pending.ShouldBeEmpty();
        _relay.Members(household)[laptop.Worker.DeviceId].Removed.ShouldNotBeNull();

        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        _clock.Advance(RelaySync.MembersEvery);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);               // sees the laptop gone, and makes a new key
        desktop.Household.Member(laptop.Worker.DeviceId).ShouldNotBeNull().LeftMs.ShouldNotBeNull();
        await desktop.Worker.RunOnceAsync(CancellationToken.None);               // which the server takes
        desktop.Worker.Store.Epoch.ShouldBe(2);
        desktop.Worker.Store.CurrentKey.ShouldNotBe(oldKey);
        _relay.Sealed(household, 2).ShouldBe([desktop.Worker.DeviceId, study.Worker.DeviceId], ignoreOrder: true);
    }

    [Fact]
    public async Task A_pc_added_again_after_a_removal_it_hadnt_heard_of_waits_for_the_server_instead_of_leaving()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        await WorkerPc.Pair(desktop, laptop);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        laptop.Worker.Store.RelayConfirmed.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await desktop.Send<HouseholdReply>(new RemovePcRequest(1, laptop.Worker.DeviceId));
        laptop.Category.IsPrivate = false;                                       // the laptop is away, and doesn't hear
        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        // The desktop's user adds the laptop again by code; the laptop is back before the desktop tells the server so.
        var code = (await desktop.Send<HouseholdReply>(new StartCodePairingRequest(2))).Code!;
        await laptop.Send<HouseholdReply>(new JoinByCodeRequest(3, code));
        await laptop.Send<HouseholdReply>(new AnswerPromptRequest(4, (await laptop.Next(NoticeKind.JoinPrompt)).PromptId!, true));
        (await laptop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("This PC joined Desktop-7's household.");
        await desktop.Worker.Running;
        laptop.Worker.Store.RelayConfirmed.ShouldBeFalse();

        await laptop.Worker.RunOnceAsync(CancellationToken.None);                // 410: the add hasn't reached the server
        laptop.Worker.Store.HouseholdId.ShouldBe(desktop.Worker.Store.HouseholdId);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);

        laptop.Worker.Store.HouseholdId.ShouldBe(desktop.Worker.Store.HouseholdId);
        laptop.Worker.Store.RelayConfirmed.ShouldBeTrue();
        laptop.Board.Household!.Problem.ShouldBeNull();
    }

    [Fact]
    public async Task Removing_a_pc_that_has_left_removes_its_rows_and_this_pc_cant_be_removed()
    {
        var (desktop, laptop, _) = await Household();
        desktop.Household.Upsert([Row(laptop.Worker.DeviceId, 0, 5, changed: 1)]);
        desktop.Household.MarkLeft(laptop.Worker.DeviceId, 1);

        (await desktop.Send<HouseholdReply>(new RemovePcRequest(1, laptop.Worker.DeviceId))).ShouldBe(
            new HouseholdReply(1, true, "Laptop-2's rows were removed."));
        (await desktop.Send<HouseholdReply>(new RemovePcRequest(2, desktop.Worker.DeviceId))).Ok.ShouldBeFalse();
        (await desktop.Send<HouseholdReply>(new RemovePcRequest(3, "0000000000000000000000000000dead"))).Ok.ShouldBeFalse();

        desktop.Household.Member(laptop.Worker.DeviceId).ShouldBeNull();
        desktop.Household.Row(laptop.Worker.DeviceId, Hour(0)).ShouldBeNull();
        desktop.Worker.Store.Tombstones.ShouldContainKey(laptop.Worker.DeviceId);   // never taken back on another's word
        desktop.Worker.Store.Epoch.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_old_rows_takes_a_left_pcs_rows_and_entry_off_this_pc_but_keeps_it_known_as_removed()
    {
        var (desktop, laptop, study) = await Household();
        desktop.Household.Upsert([Row(laptop.Worker.DeviceId, 0, 5, changed: 1), Row(study.Worker.DeviceId, 0, 6, changed: 1)]);
        desktop.Household.MarkLeft(laptop.Worker.DeviceId, 1);
        await desktop.Send<HouseholdReply>(new SetDiscoverableRequest(9, true));   // the status again
        desktop.Board.Household!.Members.Single(member => member.DeviceId == laptop.Worker.DeviceId).Left.ShouldBeTrue();

        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(1, laptop.Worker.DeviceId))).ShouldBe(
            new HouseholdReply(1, true, "Laptop-2's rows were removed."));

        desktop.Household.Row(laptop.Worker.DeviceId, Hour(0)).ShouldBeNull();
        desktop.Household.Member(laptop.Worker.DeviceId).ShouldBeNull();
        desktop.Board.Household!.Members.ShouldNotContain(member => member.DeviceId == laptop.Worker.DeviceId);
        desktop.Worker.Store.Tombstones.ShouldContainKey(laptop.Worker.DeviceId);   // never added back on another PC's word
        desktop.Household.Row(study.Worker.DeviceId, Hour(0)).ShouldNotBeNull();     // a current member's stay
    }

    [Fact]
    public async Task Old_rows_can_t_be_removed_for_this_pc_or_a_current_member()
    {
        var (desktop, laptop, _) = await Household();

        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(1, desktop.Worker.DeviceId))).ShouldBe(
            new HouseholdReply(1, false, "This PC's own rows stay while it is in the household."));
        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(2, laptop.Worker.DeviceId))).ShouldBe(
            new HouseholdReply(2, false, "Laptop-2 is still in the household. Remove it first."));
        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(3, "0000000000000000000000000000dead"))).ShouldBe(
            new HouseholdReply(3, false, "That PC has no rows on this PC."));
    }

    [Fact]
    public async Task With_no_pc_named_every_old_pcs_rows_go_and_after_leaving_that_is_all_of_them()
    {
        var (desktop, laptop, study) = await Household();
        desktop.Household.Upsert([
            Row(desktop.Worker.DeviceId, 0, 4, changed: 1), Row(laptop.Worker.DeviceId, 0, 5, changed: 1), Row(study.Worker.DeviceId, 0, 6, changed: 1)]);
        desktop.Household.MarkLeft(study.Worker.DeviceId, 1);

        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(1))).ShouldBe(new HouseholdReply(1, true, "The old rows were removed."));

        desktop.Household.Row(study.Worker.DeviceId, Hour(0)).ShouldBeNull();
        desktop.Household.Row(laptop.Worker.DeviceId, Hour(0)).ShouldNotBeNull();
        desktop.Household.Row(desktop.Worker.DeviceId, Hour(0)).ShouldNotBeNull();

        await desktop.Send<HouseholdReply>(new LeaveHouseholdRequest(2));
        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(3))).ShouldBe(new HouseholdReply(3, true, "The old rows were removed."));

        desktop.Household.Latest().ShouldBeEmpty();
        desktop.Household.Members().ShouldBeEmpty();
        (await desktop.Send<HouseholdReply>(new RemoveOldRowsRequest(4))).ShouldBe(new HouseholdReply(4, true, "There were no old rows to remove."));
    }

    [Fact]
    public async Task Renaming_checks_the_name_and_announces_it()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);

        (await desktop.Send<HouseholdReply>(new RenamePcRequest(1, "   "))).ShouldBe(new HouseholdReply(1, false, "A name has 1 to 40 characters."));
        (await desktop.Send<HouseholdReply>(new RenamePcRequest(2, new string('x', 41)))).Ok.ShouldBeFalse();
        (await desktop.Send<HouseholdReply>(new RenamePcRequest(3, " Study PC "))).ShouldBe(new HouseholdReply(3, true, "This PC is now called Study PC."));

        desktop.Worker.Store.Name.ShouldBe("Study PC");
        desktop.Board.Household!.Name.ShouldBe("Study PC");
        _network.Announced.ShouldHaveSingleItem().Txt["name"].ShouldBe("Study PC");
    }

    [Fact]
    public async Task The_tick_turns_the_announcement_and_the_listener_off_and_on()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var port = desktop.Worker.Port;
        _network.Announced.ShouldHaveSingleItem().Port.ShouldBe(port);

        (await desktop.Send<HouseholdReply>(new SetDiscoverableRequest(1, false))).ShouldBe(
            new HouseholdReply(1, true, "Other PCs on your network can no longer find this one."));
        _network.Announced.ShouldBeEmpty();
        desktop.Board.Household!.Discoverable.ShouldBeFalse();
        desktop.Worker.Port.ShouldBe(0);
        await Should.ThrowAsync<IOException>(() => LanConnector.ConnectAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5)));

        (await desktop.Send<HouseholdReply>(new SetDiscoverableRequest(2, true))).Ok.ShouldBeTrue();
        _network.Announced.ShouldHaveSingleItem().Port.ShouldBe(desktop.Worker.Port);
        desktop.Worker.Port.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Off_a_private_network_nothing_listens_and_nothing_is_announced()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);

        desktop.Category.IsPrivate = false;                                    // joined a café's network

        desktop.Worker.Port.ShouldBe(0);
        _network.Announced.ShouldBeEmpty();
        desktop.Category.IsPrivate = true;
        desktop.Worker.Port.ShouldBeGreaterThan(0);
        _network.Announced.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_pairing_that_comes_while_another_runs_is_closed_without_this_pcs_hello()
    {
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        (await laptop.Send<HouseholdReply>(new StartCodePairingRequest(1))).Ok.ShouldBeTrue();   // pairing under way

        await using var channel = await LanConnector.ConnectAsync(IPAddress.Loopback, laptop.Worker.Port, TimeSpan.FromSeconds(5));
        using var keys = DeviceKeys.Create();
        await channel.SendAsync(LanMessages.Write(LanMessages.Hello("pair", keys.DhPublic, keys, "Stranger", ChassisKind.Laptop, "c3")));

        (await channel.ReceiveAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Members_on_the_same_network_sync_directly_every_turn()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        await WorkerPc.Pair(desktop, laptop);
        _relay.Down = true;                                                       // nothing through the server
        laptop.Household.Upsert([Row(laptop.Worker.DeviceId, 3, 9, changed: Now.ToUnixTimeMilliseconds() + 5)]);

        await desktop.Worker.RunOnceAsync(CancellationToken.None);

        desktop.Household.Row(laptop.Worker.DeviceId, Hour(3)).ShouldNotBeNull().EnergyWh.ShouldBe(9);
        desktop.Board.Household!.Problem.ShouldNotBeNull().ShouldStartWith("Couldn't reach the server");
    }

    public static TheoryData<PipeRequest> Changes() =>
    [
        new AddPcRequest(1, "a1"), new StartCodePairingRequest(2), new JoinByCodeRequest(3, "K7QM-2XHD-9PW4-R8TA"), new AnswerPromptRequest(4, "p1", true),
        new RemovePcRequest(5, "0123456789abcdef0123456789abcdef"), new LeaveHouseholdRequest(6), new RenamePcRequest(7, "Study PC"),
        new SetDiscoverableRequest(8, false), new SignInRequest(9, "microsoft", "token", "salt"), new SignOutRequest(10), new DeleteAccountRequest(11),
        new CancelPairingRequest(12), new NewRecoveryCodeRequest(13), new RemoveOldRowsRequest(15), new RemoveOldRowsRequest(16, "0123456789abcdef0123456789abcdef"),
    ];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_asked_from_outside_the_console_session_is_refused(PipeRequest request)
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);

        foreach (var session in new uint?[] { WorkerPc.Screen + 1, null, NoticeHub.NoSession })
        {
            (await desktop.Worker.HandleAsync(request, session, CancellationToken.None)).ShouldBe(
                new HouseholdReply(request.Id, false, HouseholdWorker.NotAtTheScreen));
        }
        desktop.Worker.Store.Name.ShouldBe("Desktop-7");
        desktop.Worker.Store.Discoverable.ShouldBeTrue();
        (await desktop.Worker.HandleAsync(new BrowsePcsRequest(14), WorkerPc.Screen + 1, CancellationToken.None)).ShouldBeOfType<FoundPcsReply>();
    }

    [Fact]
    public async Task Signing_out_or_deleting_the_account_while_signed_out_says_so()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);

        (await desktop.Send<HouseholdReply>(new SignOutRequest(1))).ShouldBe(new HouseholdReply(1, true, "This PC isn't signed in."));
        (await desktop.Send<HouseholdReply>(new DeleteAccountRequest(2))).ShouldBe(new HouseholdReply(2, false, "Sign in first to delete your account."));
    }

    private static long Hour(int hour) => Now.AddDays(-1).AddHours(hour).ToUnixTimeMilliseconds();

    private static HouseholdRow Row(string device, int hour, double energyWh, long changed) => new(
        device, Hour(hour), energyWh, 1, 1, 1, 1, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    private async Task<WorkerPc> Start(string name, ChassisKind kind, bool appAtTheScreen = true)
    {
        var pc = new WorkerPc(name, kind, _network, _relay, _clock, appAtTheScreen);
        _pcs.Add(pc);
        await pc.Worker.StartAsync(CancellationToken.None);
        return pc;
    }

    /// <summary>Three PCs in one household, the desktop having added the other two, all known to the server.</summary>
    private async Task<(WorkerPc Desktop, WorkerPc Laptop, WorkerPc Study)> Household()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        var laptop = await Start("Laptop-2", ChassisKind.Laptop);
        var study = await Start("Study PC", ChassisKind.Desktop);
        await WorkerPc.Pair(desktop, laptop);
        await WorkerPc.Pair(desktop, study);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Worker.Store.Pending.ShouldBeEmpty();
        return (desktop, laptop, study);
    }
}
