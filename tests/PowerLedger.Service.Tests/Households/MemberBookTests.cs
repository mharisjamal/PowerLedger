using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Membership ordered by epochs, not clocks (plan 0.9): adding, removing, adding back, what a list teaches, what the
/// server's list may change, and who may hand over a key.</summary>
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
        _members.Add(Info(_other, "Laptop-2"), 1, Now);
    }

    [Fact]
    public void A_removed_pc_comes_back_only_when_added_at_an_epoch_above_its_removal()
    {
        _members.Add(Info(_study), 1, Now).ShouldBeTrue();
        _members.Remove(_study.DeviceId, Now + 1).ShouldBeTrue();

        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(1, 1));
        _members.Current(_study.DeviceId).ShouldBeNull();
        _household.Member(_study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(Now + 1);
        _members.Add(Info(_study), 1, Now + 2).ShouldBeFalse();                    // not at the epoch it was removed at
        _members.Current(_study.DeviceId).ShouldBeNull();

        _store.AddKey(2, HouseholdCrypto.NewKey());                                // the new key without it
        _members.Add(Info(_study), 2, Now + 3).ShouldBeTrue();

        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(2, 1));
        _members.Current(_study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBeNull();
    }

    [Fact]
    public void A_list_that_hasnt_heard_of_a_removal_changes_nothing_however_far_ahead_its_clock()
    {
        _members.Add(Info(_study), 1, Now);
        _members.Remove(_study.DeviceId, Now);
        var stale = Wire.Member(Info(_study)) with { Added = Now + 3 * 60_000, AddedEpoch = 1 };

        var learned = _members.Learn([stale], _other.DeviceId, _self.DeviceId, Now);

        learned.Added.ShouldBeEmpty();
        _members.Current(_study.DeviceId).ShouldBeNull();
        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(1, 1));
    }

    [Fact]
    public void A_removal_in_a_list_counts_by_its_epoch_however_far_behind_its_clock()
    {
        _members.Add(Info(_study), 1, Now);
        var removal = new WireMember(_study.DeviceId, null, null, Removed: Now - 3 * 60_000, AddedEpoch: 1, RemovedEpoch: 1);

        var learned = _members.Learn([removal], _other.DeviceId, _self.DeviceId, Now);

        learned.Removed.ShouldBe([_study.DeviceId]);
        _members.Current(_study.DeviceId).ShouldBeNull();
        _household.Member(_study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(Now - 3 * 60_000);
    }

    [Fact]
    public void An_epoch_above_this_pcs_own_plus_one_is_passed_over_so_no_list_makes_a_pc_current_for_good()
    {
        _members.Add(Info(_study), 1, Now);
        _members.Remove(_study.DeviceId, Now);
        using var ghost = DeviceKeys.Create();

        _members.Learn([Wire.Member(Info(_study)) with { AddedEpoch = 1_000_000 }], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBeEmpty();
        _members.Learn([Wire.Member(Info(ghost)) with { AddedEpoch = 3 }], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBeEmpty();

        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(1, 1));
        _household.Member(ghost.DeviceId).ShouldBeNull();
        _members.Learn([Wire.Member(Info(ghost)) with { AddedEpoch = 2 }], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBe([ghost.DeviceId]);
    }

    [Fact]
    public void A_list_never_changes_this_pcs_own_entry_nor_a_members_keys_and_its_own_pc_gives_its_name()
    {
        _members.Self(_self.DeviceId, 1);
        using var impostor = DeviceKeys.Create();
        List<WireMember> list =
        [
            new(_self.DeviceId, null, null, AddedEpoch: 1, RemovedEpoch: 1),
            Wire.Member(Info(_other, "Laptop-3")) with { AddedEpoch = 1, Dh = Wire.Encode(impostor.DhPublic) },
        ];

        _members.Learn(list, _other.DeviceId, _self.DeviceId, Now);

        _members.EpochsOf(_self.DeviceId).ShouldBe(new MemberEpochs(1));
        var other = _household.Member(_other.DeviceId).ShouldNotBeNull();
        other.DhKey.ShouldBe(_other.DhPublic);
        other.Name.ShouldBe("Laptop-2");                                            // its keys aren't the ones this PC knows it by
        _members.Learn([Wire.Member(Info(_other, "Laptop-3")) with { AddedEpoch = 1 }], _other.DeviceId, _self.DeviceId, Now);
        _household.Member(_other.DeviceId).ShouldNotBeNull().Name.ShouldBe("Laptop-3");
    }

    [Fact]
    public void The_server_removes_a_current_member_at_the_higher_of_its_add_and_this_pcs_epoch_and_never_adds_one()
    {
        _store.AddKey(2, HouseholdCrypto.NewKey());
        _store.AddKey(3, HouseholdCrypto.NewKey());                                // this PC at epoch 3
        _members.Add(Info(_study), 2, Now);

        _members.RemovedByServer(_study.DeviceId, Now + 5, serverEpoch: null).ShouldBeTrue();

        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(2, 3));
        _household.Member(_study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(Now + 5);
        _members.RemovedByServer(_study.DeviceId, Now + 6, serverEpoch: 4).ShouldBeFalse();   // removed here already: it stays as it is
        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(2, 3));
    }

    [Fact]
    public void A_removal_the_server_made_before_an_add_known_here_is_an_old_one()
    {
        _store.AddKey(2, HouseholdCrypto.NewKey());
        _store.AddKey(3, HouseholdCrypto.NewKey());
        _members.Add(Info(_study), 3, Now);                                        // added back at 3; the server hasn't heard

        _members.RemovedByServer(_study.DeviceId, Now, serverEpoch: 2).ShouldBeFalse();

        _members.Current(_study.DeviceId).ShouldNotBeNull();
    }

    [Fact]
    public void A_pc_the_server_lists_as_removed_that_this_pc_never_knew_is_kept_as_removed_at_the_servers_epoch()
    {
        _store.AddKey(2, HouseholdCrypto.NewKey());

        _members.RemovedByServer(_study.DeviceId, Now, serverEpoch: 1, serverAdded: 1).ShouldBeFalse();

        _household.Member(_study.DeviceId).ShouldBeNull();
        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(1, 1));
        _members.Learn([Wire.Member(Info(_study)) with { AddedEpoch = 1 }], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBeEmpty();
        _members.Learn([Wire.Member(Info(_study)) with { AddedEpoch = 2 }], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBe([_study.DeviceId]);
    }

    [Fact]
    public void Only_a_member_current_here_and_added_before_an_epoch_may_hand_over_its_key()
    {
        _store.AddKey(2, HouseholdCrypto.NewKey());
        _members.Add(Info(_study), 2, Now);

        _members.MaySeal(_study.DeviceId, 3).ShouldBeTrue();
        _members.MaySeal(_study.DeviceId, 2).ShouldBeFalse();                      // it was given that key
        _members.MaySeal(_other.DeviceId, 2).ShouldBeTrue();
        _members.Remove(_study.DeviceId, Now);                                      // removed at 2
        _members.MaySeal(_study.DeviceId, 3).ShouldBeFalse();                      // no allowance: it holds whatever it sealed
        _members.MaySeal("0000000000000000000000000000dead", 3).ShouldBeFalse();

        _store.AddKey(3, HouseholdCrypto.NewKey());
        _members.Add(Info(_study), 3, Now);                                        // added back at 3
        _members.MaySeal(_study.DeviceId, 4).ShouldBeTrue();
        _members.MaySeal(_study.DeviceId, 3).ShouldBeFalse();
    }

    [Fact]
    public void A_removed_pcs_rows_count_only_under_epochs_up_to_its_removal()
    {
        _members.Add(Info(_study), 1, Now);
        _store.AddKey(2, HouseholdCrypto.NewKey());
        _members.Remove(_study.DeviceId, Now);                                      // removed at 2

        (_members.MayHavePosted(_study.DeviceId, 1), _members.MayHavePosted(_study.DeviceId, 2), _members.MayHavePosted(_study.DeviceId, 3))
            .ShouldBe((true, true, false));
        _members.MayHavePosted(_other.DeviceId, 3).ShouldBeTrue();
        _members.MayHavePosted("0000000000000000000000000000dead", 1).ShouldBeFalse();
    }

    [Fact]
    public void Entries_carry_every_member_with_its_epochs_the_current_first_with_their_keys()
    {
        _members.Self(_self.DeviceId, 1);
        _household.SaveMember(new HouseholdMember(_self.DeviceId, "Desktop-7", ChassisKind.Desktop, _self.SignPublic, _self.DhPublic, Now - 1, null, null));
        _members.Add(Info(_study), 1, Now);
        _members.Remove(_study.DeviceId, Now + 1);

        var entries = _members.Entries();

        entries.Select(entry => entry.Id).ShouldBe([_self.DeviceId, _other.DeviceId, _study.DeviceId], ignoreOrder: false);
        entries[1].ShouldBe(Wire.Member(Info(_other, "Laptop-2")) with { Added = Now, AddedEpoch = 1 });
        entries[2].ShouldBe(new WireMember(_study.DeviceId, null, null, Removed: Now + 1, AddedEpoch: 1, RemovedEpoch: 1));
        _members.Entries(compact: true)[2].ShouldBe(new WireMember(_study.DeviceId, null, null, AddedEpoch: 1, RemovedEpoch: 1));
    }

    [Fact]
    public void A_removed_pc_is_kept_without_its_rows_and_only_the_64_removed_latest_are()
    {
        _members.Add(Info(_study), 1, Now);
        _members.Remove(_study.DeviceId, Now);
        _members.ForgetRows(_study.DeviceId);

        _household.Member(_study.DeviceId).ShouldBeNull();
        _members.EpochsOf(_study.DeviceId).ShouldBe(new MemberEpochs(1, 1));
        _members.Learn([Wire.Member(Info(_study)) with { AddedEpoch = 1 }], _other.DeviceId, _self.DeviceId, Now).Added.ShouldBeEmpty();

        for (var epoch = 2; epoch <= MemberBook.MaxRemoved + 1; epoch++)
        {
            _store.AddKey(epoch, HouseholdCrypto.NewKey());
            _members.Learn([new WireMember(Id(epoch), null, null, AddedEpoch: 1, RemovedEpoch: epoch)], _other.DeviceId, _self.DeviceId, Now);
        }

        _members.EpochsOf(_study.DeviceId).ShouldBeNull();                          // removed earliest, so it went first
        _members.EpochsOf(Id(2)).ShouldNotBeNull();
        _members.Entries().Count(entry => entry.RemovedEpoch is not null).ShouldBe(MemberBook.MaxRemoved);
    }

    public void Dispose()
    {
        foreach (var keys in new[] { _self, _other, _study }) keys.Dispose();
        _database.Dispose();
    }

    private static string Id(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static MemberInfo Info(DeviceKeys keys, string name = "Study PC") => new(keys.DeviceId, name, ChassisKind.Desktop, keys.SignPublic, keys.DhPublic);
}
