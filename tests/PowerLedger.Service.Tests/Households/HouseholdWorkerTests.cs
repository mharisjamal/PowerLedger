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
        (prompt.Text, prompt.ComparisonCode).ShouldBe(("Join Desktop-7's household?", (string?)null));
        await laptop.Send<HouseholdReply>(new AnswerPromptRequest(4, prompt.PromptId!, true));

        (await laptop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("This PC joined Desktop-7's household.");
        (await desktop.Next(NoticeKind.PairingProgress)).Text.ShouldBe("Laptop-2 joined your household.");
        laptop.Worker.Store.HouseholdId.ShouldBe(desktop.Worker.Store.HouseholdId);
    }

    [Fact]
    public async Task Removing_a_pc_changes_the_key_for_those_who_stay_and_the_removed_pc_cant_read_what_comes_after()
    {
        var (desktop, laptop, study) = await Household();
        desktop.Worker.Store.HistoryPosted = [desktop.Worker.DeviceId, laptop.Worker.DeviceId, study.Worker.DeviceId];
        foreach (var pc in new[] { desktop, laptop, study }) await pc.Worker.RunOnceAsync(CancellationToken.None);
        var oldKey = study.Worker.Store.CurrentKey.ShouldNotBeNull();

        (await desktop.Send<HouseholdReply>(new RemovePcRequest(1, study.Worker.DeviceId))).ShouldBe(
            new HouseholdReply(1, true, "Study PC was removed from the household."));

        desktop.Worker.Store.Epoch.ShouldBe(2);
        var pending = desktop.Worker.Store.Pending;
        pending.Select(op => op.Kind).ShouldBe([PendingOp.Remove, PendingOp.Keys]);
        pending[1].Envelopes.ShouldNotBeNull().Select(envelope => envelope.Device).ShouldBe([desktop.Worker.DeviceId, laptop.Worker.DeviceId], ignoreOrder: true);
        desktop.Board.Household!.Members.Single(member => member.DeviceId == study.Worker.DeviceId).Left.ShouldBeTrue();

        desktop.Household.Upsert([Row(desktop.Worker.DeviceId, 1, 42, changed: Now.ToUnixTimeMilliseconds() + 1)]);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        await study.Worker.RunOnceAsync(CancellationToken.None);

        laptop.Worker.Store.Epoch.ShouldBe(2);
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
    public async Task Leaving_seals_a_new_key_for_those_who_stay_before_taking_this_pc_off_the_server()
    {
        var (desktop, laptop, study) = await Household();
        var household = laptop.Worker.Store.HouseholdId!;

        (await laptop.Send<HouseholdReply>(new LeaveHouseholdRequest(1))).ShouldBe(new HouseholdReply(1, true, "This PC left the household."));

        laptop.Worker.Store.HouseholdId.ShouldBeNull();
        laptop.Board.Household.ShouldNotBeNull().Members.ShouldBeEmpty();
        var pending = laptop.Worker.Store.Pending;
        pending.Select(op => op.Kind).ShouldBe([PendingOp.Keys, PendingOp.Remove]);
        pending[0].Envelopes!.Select(envelope => envelope.Device).ShouldBe([desktop.Worker.DeviceId, study.Worker.DeviceId], ignoreOrder: true);
        (await laptop.Send<HouseholdReply>(new LeaveHouseholdRequest(2))).ShouldBe(new HouseholdReply(2, false, HouseholdWorker.NotInOne));

        await laptop.Worker.RunOnceAsync(CancellationToken.None);
        laptop.Worker.Store.Pending.ShouldBeEmpty();
        _relay.Members(household)[laptop.Worker.DeviceId].Removed.ShouldNotBeNull();

        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        _clock.Advance(RelaySync.MembersEvery);
        await desktop.Worker.RunOnceAsync(CancellationToken.None);
        desktop.Board.Household!.Members.Single(member => member.DeviceId == laptop.Worker.DeviceId).Left.ShouldBeTrue();
        desktop.Worker.Store.Epoch.ShouldBe(2);                                  // the key the laptop sealed as it left
        desktop.Worker.Store.CurrentKey.ShouldNotBe(laptop.Worker.Store.CurrentKey);
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
        desktop.Worker.Store.Epoch.ShouldBe(1);
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
    public async Task The_tick_turns_the_announcement_off_and_on()
    {
        var desktop = await Start("Desktop-7", ChassisKind.Desktop);
        _network.Announced.ShouldHaveSingleItem().Port.ShouldBe(desktop.Worker.Port);

        (await desktop.Send<HouseholdReply>(new SetDiscoverableRequest(1, false))).ShouldBe(
            new HouseholdReply(1, true, "Other PCs on your network can no longer find this one."));
        _network.Announced.ShouldBeEmpty();
        desktop.Board.Household!.Discoverable.ShouldBeFalse();

        (await desktop.Send<HouseholdReply>(new SetDiscoverableRequest(2, true))).Ok.ShouldBeTrue();
        _network.Announced.ShouldHaveSingleItem();
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
