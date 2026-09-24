using System.Security.Cryptography;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Where the household keeps this PC's keys, the household's keys and its progress: the settings table, the secrets
/// DPAPI-protected (households design §1).</summary>
public sealed class HouseholdStoreTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private SettingsRepository Settings => new(_database.Db);

    private HouseholdStore Store() => new(Settings, () => "DESKTOP-7");

    [Fact]
    public void This_pcs_keys_are_made_once_and_kept_protected()
    {
        using var keys = Store().DeviceKeys();
        using var again = Store().DeviceKeys();

        again.DeviceId.ShouldBe(keys.DeviceId);
        again.SignPublic.ShouldBe(keys.SignPublic);
        again.DhPublic.ShouldBe(keys.DhPublic);
        var (sign, dh) = keys.ExportPrivate();
        Settings.Get(HouseholdStore.SignKey).ShouldNotBeNull().ShouldNotBe(Convert.ToBase64String(sign));
        Settings.Get(HouseholdStore.DhKey).ShouldNotBeNull().ShouldNotBe(Convert.ToBase64String(dh));
        Store().DeviceId.ShouldBe(keys.DeviceId);
    }

    [Fact]
    public void Nothing_is_held_before_a_household()
    {
        var store = Store();

        (store.HouseholdId, store.Epoch, store.CurrentKey, store.KeyFor(1), store.Session).ShouldBe((null, 0, null, null, null));
        (store.RelayCursor, store.Discoverable, store.Name).ShouldBe((0L, true, "DESKTOP-7"));
    }

    [Fact]
    public void A_household_its_keys_by_epoch_and_its_progress_round_trip()
    {
        var first = HouseholdCrypto.NewKey();
        var second = HouseholdCrypto.NewKey();
        var store = Store();
        store.EnterHousehold("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, first);
        store.AddKey(2, second);
        store.RelayCursor = 17;
        store.NextSequence().ShouldBe(1);
        store.NextSequence().ShouldBe(2);

        var again = Store();
        again.HouseholdId.ShouldBe("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f");
        again.Epoch.ShouldBe(2);
        again.CurrentKey.ShouldBe(second);
        again.KeyFor(1).ShouldBe(first);
        again.KeyFor(3).ShouldBeNull();
        again.RelayCursor.ShouldBe(17);
        again.NextSequence().ShouldBe(3);
        Settings.All().Values.ShouldNotContain(value => value.Contains(Convert.ToBase64String(first)));

        again.AddKey(1, second);                                    // an older epoch arriving late never goes back
        Store().Epoch.ShouldBe(2);
        Store().KeyFor(1).ShouldBe(first);
    }

    [Fact]
    public void Entering_a_household_starts_its_progress_afresh_and_leaving_forgets_it_but_not_this_pc()
    {
        var store = Store();
        var deviceId = store.DeviceId;
        store.EnterHousehold("aaaa0000aaaa0000aaaa0000aaaa0000", 1, HouseholdCrypto.NewKey());
        store.RelayCursor = 40;
        store.NextSequence();
        store.EnterHousehold("bbbb0000bbbb0000bbbb0000bbbb0000", 3, HouseholdCrypto.NewKey());

        (store.HouseholdId, store.Epoch, store.RelayCursor, store.KeyFor(1)).ShouldBe(("bbbb0000bbbb0000bbbb0000bbbb0000", 3, 0L, null));
        store.NextSequence().ShouldBe(1);

        store.LeaveHousehold();
        (Store().HouseholdId, Store().Epoch, Store().CurrentKey, Store().KeyFor(3), Store().RelayCursor).ShouldBe((null, 0, null, null, 0L));
        Store().DeviceId.ShouldBe(deviceId);
    }

    [Fact]
    public void Discoverable_the_instance_id_the_name_and_the_session_are_kept()
    {
        var store = Store();
        var instance = store.InstanceId;
        instance.Length.ShouldBe(32);
        store.Discoverable = false;
        store.Name = "Kitchen laptop";
        store.Session = "c2Vzc2lvbi10b2tlbg";

        var again = Store();
        again.InstanceId.ShouldBe(instance);
        again.Discoverable.ShouldBeFalse();
        again.Name.ShouldBe("Kitchen laptop");
        again.Session.ShouldBe("c2Vzc2lvbi10b2tlbg");
        Settings.Get(HouseholdStore.SessionKey).ShouldNotBe("c2Vzc2lvbi10b2tlbg");

        again.Session = null;
        Store().Session.ShouldBeNull();
    }

    [Fact]
    public void Relay_progress_goes_with_the_household_but_what_the_server_still_has_to_hear_stays()
    {
        var store = Store();
        store.EnterHousehold("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey());
        store.PostedThrough = 1_234;
        store.HistoryPosted = ["a", "b"];
        store.RelayConfirmed = true;
        store.MembersCheckedAt = 5_678;
        store.Problem = "Couldn't sync through the server: it was down.";
        store.AddPending(new Households.Relay.PendingOp(Households.Relay.PendingOp.Remove, "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", Device: "c"));

        var again = Store();
        (again.PostedThrough, again.RelayConfirmed, again.MembersCheckedAt, again.Problem)
            .ShouldBe((1_234L, true, (long?)5_678, "Couldn't sync through the server: it was down."));
        again.HistoryPosted.ShouldBe(["a", "b"]);

        again.LeaveHousehold();
        var left = Store();
        (left.PostedThrough, left.RelayConfirmed, left.MembersCheckedAt, left.Problem).ShouldBe((0L, false, (long?)null, (string?)null));
        left.HistoryPosted.ShouldBeEmpty();
        left.Pending.ShouldHaveSingleItem().Device.ShouldBe("c");
    }

    [Fact]
    public void What_cant_be_decrypted_counts_as_none()
    {
        var store = Store();
        var deviceId = store.DeviceId;
        store.EnterHousehold("5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f", 1, HouseholdCrypto.NewKey());
        store.Session = "c2Vzc2lvbi10b2tlbg";
        var junk = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        Settings.Set(HouseholdStore.KeysKey, junk);
        Settings.Set(HouseholdStore.SessionKey, "not base64 at all");

        (Store().CurrentKey, Store().KeyFor(1), Store().Session).ShouldBe((null, null, null));

        // Keys this account can't read, as in a database from another PC: this PC starts again under new keys, and the
        // household, which knew it by the old ones, is forgotten.
        Settings.Set(HouseholdStore.SignKey, junk);
        var fresh = Store();
        fresh.DeviceId.ShouldNotBe(deviceId);
        fresh.HouseholdId.ShouldBeNull();
        Store().DeviceId.ShouldBe(fresh.DeviceId);
    }

    public void Dispose() => _database.Dispose();
}
