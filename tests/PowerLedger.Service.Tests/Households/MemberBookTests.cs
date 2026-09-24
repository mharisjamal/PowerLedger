using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Membership as plan 0.10 has it: the server's list says who is in, introductions say whose keys to trust, lists'
/// removals only stop sync on the network and show a PC as left, and the server's epochs say who may hand over a key.</summary>
public sealed class MemberBookTests : IDisposable
{
    private const string Household = "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f";
    private static readonly long Now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly TestDatabase _database = new();
    private readonly HouseholdStore _store;
    private readonly HouseholdRepository _household;
    private readonly MemberBook _members;
    private readonly DeviceKeys _self = DeviceKeys.Create();
    private readonly DeviceKeys _other = DeviceKeys.Create();
    private readonly DeviceKeys _study = DeviceKeys.Create();

    public MemberBookTests()
    {
        _store = new HouseholdStore(new SettingsRepository(_database.Db), () => "Desktop-7");
        _household = new HouseholdRepository(_database.Db);
        _members = new MemberBook(_store, _household);
        _store.EnterHousehold(Household, 1, HouseholdCrypto.NewKey());
        _household.SaveMember(new HouseholdMember(_self.DeviceId, "Desktop-7", ChassisKind.Desktop, _self.SignPublic, _self.DhPublic, Now - 1, null, null));
        _members.Introduce(Info(_other, "Laptop-2"), Now);
    }

    [Fact]
    public void A_pc_is_current_only_when_the_servers_list_has_it_and_an_introduction_brought_its_keys()
    {
        using var planted = DeviceKeys.Create();

        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(planted, 1)], _self.DeviceId, Now);

        _members.Current(_other.DeviceId).ShouldNotBeNull();
        _members.Current(planted.DeviceId).ShouldBeNull();                          // the server's word alone brings in nobody
        _household.Member(planted.DeviceId).ShouldBeNull();
        _members.Introduce(Info(_study), Now);
        _members.Current(_study.DeviceId).ShouldBeNull();                           // introduced, but the server doesn't have it

        _store.AddPending(new PendingOp(PendingOp.Add, Household, Sign: Wire.Encode(_study.SignPublic), Dh: Wire.Encode(_study.DhPublic)));

        _members.Current(_study.DeviceId).ShouldNotBeNull();                        // this PC's own add, waiting for the server
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1, removedAt: 1, at: Now + 1)], _self.DeviceId, Now + 2);
        _members.Current(_other.DeviceId).ShouldBeNull();
        _household.Member(_other.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(Now + 1);   // as the server removed it
    }

    [Fact]
    public void Before_this_pc_reads_the_servers_list_the_introductions_alone_decide()
    {
        _members.Current(_other.DeviceId).ShouldNotBeNull();

        _members.Learn([Removal(_other, removedAt: 1)], _study.DeviceId, _self.DeviceId, Now);   // from a PC not introduced here
        _members.Current(_other.DeviceId).ShouldNotBeNull();

        _members.Remove(_other.DeviceId, Now + 2).ShouldBeTrue();
        _members.Current(_other.DeviceId).ShouldBeNull();
        _household.Member(_other.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(Now + 2);
    }

    [Fact]
    public void A_lists_entry_about_its_own_pc_counts_for_nothing_and_no_list_says_anything_of_this_pc()
    {
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1)], _self.DeviceId, Now);
        using var impostor = DeviceKeys.Create();
        List<WireMember> list =
        [
            Removal(_self, removedAt: 5),
            Wire.Member(Info(_other, "Laptop-3")) with { AddedEpoch = 9, Dh = Wire.Encode(impostor.DhPublic) },
            Removal(_other, removedAt: 9),
        ];

        _members.Learn(list, _other.DeviceId, _self.DeviceId, Now);

        _members.ShownRemoved(_other.DeviceId).ShouldBeFalse();                     // no removal of itself
        _members.ShownRemoved(_self.DeviceId).ShouldBeFalse();
        var other = _household.Member(_other.DeviceId).ShouldNotBeNull();
        other.DhKey.ShouldBe(_other.DhPublic);                                     // no keys of its own
        other.Name.ShouldBe("Laptop-2");
        _members.Learn([Wire.Member(Info(_other, "Laptop-3"))], _other.DeviceId, _self.DeviceId, Now);
        _household.Member(_other.DeviceId).ShouldNotBeNull().Name.ShouldBe("Laptop-3");   // its name and kind, with the keys known here
    }

    [Fact]
    public void A_removal_in_a_current_members_list_stops_sync_and_shows_the_pc_as_left_but_the_server_still_has_it_in()
    {
        _members.Introduce(Info(_study), Now);
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1)], _self.DeviceId, Now);

        var learned = _members.Learn([Removal(_study, removedAt: 1, at: Now - 3 * 60_000)], _other.DeviceId, _self.DeviceId, Now);

        learned.Removed.ShouldBe([_study.DeviceId]);
        _members.MaySync(_study.DeviceId).ShouldBeFalse();
        _household.Member(_study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(Now);
        _members.Current(_study.DeviceId).ShouldNotBeNull();                        // current for the next key, as the server lists it
        _members.ServerCurrent(_study.DeviceId).ShouldBeTrue();
    }

    [Fact]
    public void A_lists_removal_counts_while_its_pc_is_current_until_the_server_adds_the_pc_again_or_the_list_drops_it()
    {
        _members.Introduce(Info(_study), Now);
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1)], _self.DeviceId, Now);
        _members.Learn([Removal(_study, removedAt: 1)], _other.DeviceId, _self.DeviceId, Now);
        _members.ShownRemoved(_study.DeviceId).ShouldBeTrue();

        _members.TakeServerList([Listed(_self, 2), Listed(_other, 1), Listed(_study, 2)], _self.DeviceId, Now);   // added again at 2
        _members.ShownRemoved(_study.DeviceId).ShouldBeFalse();
        _household.Member(_study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBeNull();

        _members.Learn([Removal(_study, removedAt: 2)], _other.DeviceId, _self.DeviceId, Now);
        _members.ShownRemoved(_study.DeviceId).ShouldBeTrue();
        _members.Learn([Wire.Member(Info(_other, "Laptop-2"))], _other.DeviceId, _self.DeviceId, Now);   // its next list without it
        _members.ShownRemoved(_study.DeviceId).ShouldBeFalse();

        _members.Learn([Removal(_study, removedAt: 2)], _other.DeviceId, _self.DeviceId, Now);
        _members.TakeServerList([Listed(_self, 2), Listed(_other, 1, removedAt: 2), Listed(_study, 2)], _self.DeviceId, Now);
        _members.ShownRemoved(_study.DeviceId).ShouldBeFalse();                    // its PC is no longer current
    }

    [Fact]
    public void Only_a_pc_introduced_here_and_added_before_an_epoch_and_not_removed_before_it_may_hand_over_its_key()
    {
        _members.Introduce(Info(_study), Now);
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 2, removedAt: 4)], _self.DeviceId, Now);

        _members.MaySeal(_study.DeviceId, 2).ShouldBeFalse();                      // it was given that key
        _members.MaySeal(_study.DeviceId, 3).ShouldBeTrue();
        _members.MaySeal(_study.DeviceId, 4).ShouldBeTrue();                       // the key it made before it went is still its own
        _members.MaySeal(_study.DeviceId, 5).ShouldBeFalse();
        _members.MaySeal(_other.DeviceId, 5).ShouldBeTrue();
        using var planted = DeviceKeys.Create();
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(planted, 1)], _self.DeviceId, Now);
        _members.MaySeal(planted.DeviceId, 3).ShouldBeFalse();                     // never introduced here
    }

    [Fact]
    public void A_removed_pcs_rows_count_only_under_epochs_up_to_its_removal_on_the_server()
    {
        _members.Introduce(Info(_study), Now);
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1, removedAt: 2)], _self.DeviceId, Now);

        (_members.MayHavePosted(_study.DeviceId, 1), _members.MayHavePosted(_study.DeviceId, 2), _members.MayHavePosted(_study.DeviceId, 3))
            .ShouldBe((true, true, false));
        _members.MayHavePosted(_other.DeviceId, 3).ShouldBeTrue();
        _members.MayHavePosted("0000000000000000000000000000dead", 1).ShouldBeFalse();
    }

    [Fact]
    public void Entries_carry_each_pc_shown_as_in_with_its_keys_then_each_removed_one_with_its_epochs()
    {
        _members.Introduce(Info(_study), Now);
        using var gone = DeviceKeys.Create();
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 2), Listed(gone, 1, removedAt: 1, at: Now - 5)], _self.DeviceId, Now);
        _members.Remove(_study.DeviceId, Now + 1);

        var entries = _members.Entries();

        entries.Select(entry => entry.Id).ShouldBe([_self.DeviceId, _other.DeviceId, _study.DeviceId, gone.DeviceId], ignoreOrder: false);
        entries[1].ShouldBe(Wire.Member(Info(_other, "Laptop-2")) with { Added = Now, AddedEpoch = 1 });
        entries[2].ShouldBe(new WireMember(_study.DeviceId, null, null, Removed: Now + 1, AddedEpoch: 2, RemovedEpoch: 2));
        entries[3].ShouldBe(new WireMember(gone.DeviceId, null, null, Removed: Now - 5, AddedEpoch: 1, RemovedEpoch: 1));
        _members.Entries(compact: true)[2].ShouldBe(new WireMember(_study.DeviceId, null, null, AddedEpoch: 2, RemovedEpoch: 2));
    }

    [Fact]
    public void A_pc_the_server_lists_that_nobody_introduces_is_to_be_taken_out_after_three_days()
    {
        using var planted = DeviceKeys.Create();
        var day = (long)TimeSpan.FromDays(1).TotalMilliseconds;

        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(planted, 1)], _self.DeviceId, Now).Unknown.ShouldBeEmpty();
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(planted, 1)], _self.DeviceId, Now + 2 * day).Unknown.ShouldBeEmpty();

        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(planted, 1)], _self.DeviceId, Now + 3 * day).Unknown.ShouldBe([planted.DeviceId]);
        _members.Learn([Wire.Member(Info(planted, "Den PC"))], _other.DeviceId, _self.DeviceId, Now + 3 * day);   // introduced at last
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(planted, 1)], _self.DeviceId, Now + 4 * day).Unknown.ShouldBeEmpty();
        _members.Current(planted.DeviceId).ShouldNotBeNull();
    }

    [Fact]
    public void A_pc_removed_here_or_on_the_server_isnt_brought_back_by_a_list_once_its_rows_went()
    {
        _members.Introduce(Info(_study), Now);
        _members.Remove(_study.DeviceId, Now);
        _members.ForgetRows(_study.DeviceId);
        using var gone = DeviceKeys.Create();
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1), Listed(gone, 1, removedAt: 1)], _self.DeviceId, Now);

        _members.Learn([Wire.Member(Info(_study)), Wire.Member(Info(gone, "Den PC"))], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBeEmpty();

        _household.Member(_study.DeviceId).ShouldBeNull();
        _household.Member(gone.DeviceId).ShouldBeNull();
        _members.Introduce(Info(_study), Now);                                     // this PC's own pairing brings it back
        _members.ShownRemoved(_study.DeviceId).ShouldBeFalse();
    }

    [Fact]
    public void A_pc_current_here_that_the_server_removes_is_gone_and_one_removed_earlier_isnt()
    {
        _members.Introduce(Info(_study), Now);
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1)], _self.DeviceId, Now);

        var learned = _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1, removedAt: 1)], _self.DeviceId, Now);

        learned.Gone.ShouldBe([_study.DeviceId]);
        learned.Left.ShouldBe([_study.DeviceId]);
        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 1, removedAt: 1)], _self.DeviceId, Now).Gone.ShouldBeEmpty();
    }

    [Fact]
    public void A_key_a_list_gave_for_a_pc_that_differs_from_the_one_the_server_lists_counts_for_nothing_until_a_list_gives_the_servers()
    {
        using var planted = DeviceKeys.Create();
        var rogue = new WireMember(_study.DeviceId, "Study PC", "desktop", Wire.Encode(_study.SignPublic), Wire.Encode(planted.DhPublic));
        _members.Learn([rogue], _other.DeviceId, _self.DeviceId, Now);             // before its add: its own signing key, another's key agreement

        _members.TakeServerList([Listed(_self, 1), Listed(_other, 1), Listed(_study, 2)], _self.DeviceId, Now);   // then its add, with its own keys

        _members.Current(_study.DeviceId).ShouldBeNull();                           // nothing sealed to the key a list planted
        _members.MaySync(_study.DeviceId).ShouldBeFalse();
        _members.MaySeal(_study.DeviceId, 3).ShouldBeFalse();
        _members.Learn([Wire.Member(Info(_study))], _other.DeviceId, _self.DeviceId, Now);   // a list with the keys the server has
        _members.Current(_study.DeviceId).ShouldNotBeNull().DhKey.ShouldBe(_study.DhPublic);
        _members.Learn([rogue], _other.DeviceId, _self.DeviceId, Now);             // and no list takes them away again
        _household.Member(_study.DeviceId).ShouldNotBeNull().DhKey.ShouldBe(_study.DhPublic);
    }

    public void Dispose()
    {
        foreach (var keys in new[] { _self, _other, _study }) keys.Dispose();
        _database.Dispose();
    }

    /// <summary>A PC as the server lists it, added at <paramref name="added"/> and removed at <paramref name="removedAt"/>.</summary>
    internal static ServerMember Listed(DeviceKeys keys, int added, int? removedAt = null, long? at = null) =>
        new(keys.DeviceId, Wire.Encode(keys.SignPublic), Wire.Encode(keys.DhPublic), Now, removedAt is null ? null : at ?? Now, added, removedAt);

    private static WireMember Removal(DeviceKeys keys, int removedAt, long? at = null) =>
        new(keys.DeviceId, null, null, Removed: at, AddedEpoch: 1, RemovedEpoch: removedAt);

    private static MemberInfo Info(DeviceKeys keys, string name = "Study PC") => new(keys.DeviceId, name, ChassisKind.Desktop, keys.SignPublic, keys.DhPublic);
}
