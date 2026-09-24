using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Who is in, and who a new key goes to, through the server (plan 0.10): the server's list says who is in, a PC
/// nobody introduced is taken out after three days, and a removal known only from a list keeps nobody out of a new key. A
/// new key that loses its epoch rotates on from the server's, always when it was made for a removal, while fetching goes
/// on; and a PC behind the server's epoch posts nothing under the key it has.</summary>
public sealed class RelayMembershipTests : IDisposable
{
    private const string Household = "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f";
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly long NowMs = Now.ToUnixTimeMilliseconds();

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRelay _relay;
    private readonly byte[] _key1 = HouseholdCrypto.NewKey();
    private readonly List<Pc> _pcs = [];

    public RelayMembershipTests()
    {
        _relay = new FakeRelay(_clock);
    }

    [Fact]
    public async Task A_pc_the_server_lists_that_nobody_introduced_is_taken_out_after_three_days_and_the_new_key_goes()
    {
        var (a, b, r) = (NewPc("Desktop-A"), NewPc("Laptop-B"), NewPc("Rogue-R"));
        InHousehold(a, b, r);
        using var puppet = DeviceKeys.Create();                                    // R adds a PC of its own that it never lists
        (await r.Client.AddMemberAsync(r.Keys, Household, puppet.SignPublic, puppet.DhPublic, Wire.SignJoin(puppet, Household), CancellationToken.None))
            .Ok.ShouldBeTrue();
        a.Members.Remove(r.Id, NowMs).ShouldBeTrue();
        a.Store.AddPending(new PendingOp(PendingOp.Remove, Household, Device: r.Id));
        a.Sync.StartRotation(Household, forRemoval: true);
        a.Household.Upsert([Row(a.Id, 1, 10, changed: NowMs + 1)]);
        b.Household.Upsert([Row(b.Id, 2, 20, changed: NowMs + 1)]);
        await b.RunAsync();

        List<RelayRun> runs = [];
        for (var day = 0; day < 4; day++)
        {
            runs.Add(await a.RunAsync());
            _clock.Advance(TimeSpan.FromDays(1));
        }

        runs.ShouldAllBe(run => run.Problem == null);
        runs[0].RowsIn.ShouldBe(1);                                                  // what the others sent comes in while the key waits
        runs.Take(3).ShouldAllBe(run => run.RowsOut == 0);                           // and nothing goes up under the old key
        runs.SelectMany(run => run.Notices).ShouldContain(RelaySync.NeverIntroduced);
        _relay.Members(Household)[puppet.DeviceId].Removed.ShouldNotBeNull();
        _relay.Epoch(Household).ShouldBe(2);
        _relay.Sealed(Household, 2).ShouldBe([a.Id, b.Id], ignoreOrder: true);
        _relay.Batches.Where(batch => batch.Device == a.Id).ShouldNotBeEmpty();
        _relay.Batches.Where(batch => batch.Device == a.Id).ShouldAllBe(batch => batch.Epoch == 2);
    }

    [Fact]
    public async Task A_removal_known_only_from_a_members_list_stops_sync_but_keeps_nobody_out_of_the_new_key()
    {
        var (b, m, d) = (NewPc("Laptop-B"), NewPc("Desktop-M"), NewPc("Desktop-D"));
        InHousehold(b, m, d);
        await b.RunAsync();                                                        // the members read
        b.Members.Learn([new WireMember(d.Id, null, null, AddedEpoch: 1, RemovedEpoch: 2)], m.Id, b.Id, NowMs).Removed.ShouldBe([d.Id]);
        b.Sync.StartRotation(Household);
        b.Household.Upsert([Row(b.Id, 1, 10, changed: NowMs + 1)]);

        (await b.RunAsync()).Problem.ShouldBeNull();

        b.Members.MaySync(d.Id).ShouldBeFalse();                                   // shown as left, and not synced with on the network
        b.Household.Member(d.Id).ShouldNotBeNull().LeftMs.ShouldNotBeNull();
        _relay.Epoch(Household).ShouldBe(2);
        _relay.Sealed(Household, 2).ShouldBe([b.Id, m.Id, d.Id], ignoreOrder: true);   // the server's list decides who is in
        _relay.Batches.Where(batch => batch.Device == b.Id).ShouldAllBe(batch => batch.Epoch == 2);
        _relay.Members(Household)[d.Id].Removed.ShouldBeNull();                    // nothing is taken out on a list's word
    }

    [Fact]
    public async Task With_a_server_that_hides_a_removal_the_next_key_still_goes_to_the_pc_it_lists_which_is_not_claimed()
    {
        var (a, b, r) = (NewPc("Desktop-A"), NewPc("Laptop-B"), NewPc("Rogue-R"));
        InHousehold(a, b, r);
        await b.RunAsync();
        b.Members.Learn([new WireMember(r.Id, "Rogue-R", "desktop", Wire.Encode(r.Keys.SignPublic), Wire.Encode(r.Keys.DhPublic), AddedEpoch: 2)],
            r.Id, b.Id, NowMs);                                                    // R's own entry counts for nothing
        b.Members.Learn([new WireMember(r.Id, null, null, AddedEpoch: 1, RemovedEpoch: 1)], a.Id, b.Id, NowMs);   // A's list: R removed at 1
        b.Sync.StartRotation(Household);                                          // the server never hears of it: a removed PC working with it

        (await b.RunAsync()).Problem.ShouldBeNull();

        b.Members.MaySync(r.Id).ShouldBeFalse();
        _relay.Sealed(Household, 2).ShouldContain(r.Id);                           // plan 0.10: a removed PC working with the server isn't claimed
    }

    [Fact]
    public async Task A_new_key_for_a_removal_that_loses_its_epoch_rotates_on_whatever_epoch_the_server_says_the_removal_was_at()
    {
        var (p, x, d) = (NewPc("Desktop-P"), NewPc("Laptop-X"), NewPc("Gone-D"));
        InHousehold(p, x, d);
        var key2 = HouseholdCrypto.NewKey();                                       // X's new key came first, sealed to D, then current
        (await x.Client.PostKeysAsync(x.Keys, Household, 2, KeyWrap.For(x.Keys, Household, 2, key2, [p.AsMember(), x.AsMember(), d.AsMember()]),
            CancellationToken.None)).Ok.ShouldBeTrue();
        p.Members.Remove(d.Id, NowMs).ShouldBeTrue();
        p.Store.AddPending(new PendingOp(PendingOp.Remove, Household, Device: d.Id));
        p.Sync.StartRotation(Household, forRemoval: true);
        p.Household.Upsert([Row(p.Id, 4, 44, changed: NowMs + 1)]);
        _relay.Intercept = (request, _) =>
        {
            if (request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal)) return null;
            return FakeRelay.Json(new JsonObject
            {
                ["members"] = new JsonArray([.. _relay.Members(Household).Select(pair => (JsonNode)new JsonObject
                {
                    ["device"] = pair.Key, ["sign"] = pair.Value.Sign, ["dh"] = pair.Value.Dh, ["added"] = pair.Value.Added,
                    ["removed"] = pair.Value.Removed, ["addedEpoch"] = pair.Value.AddedEpoch,
                    ["removedEpoch"] = pair.Value.Removed is null ? null : 1,      // the server says 1; it was 2
                })]),
            });
        };

        (await p.RunAsync()).Problem.ShouldBeNull();

        p.Store.KeyFor(2).ShouldBe(key2);                                          // to read what came under it
        p.Store.Epoch.ShouldBe(3);
        _relay.Sealed(Household, 3).ShouldBe([p.Id, x.Id], ignoreOrder: true);
        var mine = _relay.Batches.Where(batch => batch.Device == p.Id).ToList();
        mine.ShouldNotBeEmpty();
        mine.ShouldAllBe(batch => batch.Epoch == 3);                               // never under the key D holds
    }

    [Fact]
    public async Task A_rotation_that_lost_its_epoch_rotates_on_from_the_servers_epoch_and_fetching_goes_on_meanwhile()
    {
        var (desktop, laptop, gone) = (NewPc("Desktop-7"), NewPc("Laptop-2"), NewPc("Gone-D"));
        InHousehold(desktop, laptop, gone);
        _relay.Remove(Household, gone.Id);                                         // the laptop removes D and rotates to 2
        laptop.Members.Remove(gone.Id, NowMs);
        var key2 = HouseholdCrypto.NewKey();
        (await laptop.Client.PostKeysAsync(laptop.Keys, Household, 2, KeyWrap.For(laptop.Keys, Household, 2, key2, [desktop.AsMember(), laptop.AsMember()]),
            CancellationToken.None)).Ok.ShouldBeTrue();
        var study = NewPc("Study PC");                                             // welcomed by the desktop at 1, with D still in its list
        study.Store.EnterHousehold(Household, 1, _key1);
        foreach (var member in new[] { desktop, laptop, gone, study }) study.Household.SaveMember(member.AsMember());
        _relay.Seed(Household, study.Keys);                                        // the desktop's add reaches the server, at 2
        (await study.RunAsync()).Notices.ShouldBe(["Gone-D is no longer in the household."]);   // a new key of its own waits, for 2
        var key3 = HouseholdCrypto.NewKey();                                       // then the laptop rotates again, sealed to the study PC too
        (await laptop.Client.PostKeysAsync(laptop.Keys, Household, 3, KeyWrap.For(laptop.Keys, Household, 3, key3,
            [desktop.AsMember(), laptop.AsMember(), study.AsMember()]), CancellationToken.None)).Ok.ShouldBeTrue();
        laptop.Store.AddKey(2, key2);
        laptop.Store.AddKey(3, key3);
        laptop.Household.SaveMember(study.AsMember());                              // the desktop's list introduced it there
        laptop.Household.Upsert([Row(laptop.Id, 2, 20, changed: NowMs + 5)]);
        (await laptop.RunAsync()).RowsOut.ShouldBeGreaterThan(0);
        var posts = _relay.Posted($"POST /v1/households/{Household}/keys");

        var run = await study.RunAsync();

        run.Problem.ShouldBeNull();
        study.Household.Row(laptop.Id, Hour(2)).ShouldNotBeNull().EnergyWh.ShouldBe(20);
        study.Store.KeyFor(3).ShouldBe(key3);
        study.Store.Epoch.ShouldBe(4);                                             // made for a removal: rotated on from the server's 3
        _relay.Sealed(Household, 4).ShouldBe([desktop.Id, laptop.Id, study.Id], ignoreOrder: true);
        study.Store.Pending.ShouldBeEmpty();
        (_relay.Posted($"POST /v1/households/{Household}/keys") - posts).ShouldBe(2);   // the one refused, then the one after the server's epoch
    }

    [Fact]
    public async Task A_pc_added_across_a_rotation_posts_nothing_under_the_key_it_was_welcomed_with_and_makes_a_newer_one()
    {
        var (desktop, laptop, gone) = (NewPc("Desktop-7"), NewPc("Laptop-2"), NewPc("Gone-D"));
        InHousehold(desktop, laptop, gone);
        _relay.Remove(Household, gone.Id);
        (await laptop.Client.PostKeysAsync(laptop.Keys, Household, 2, KeyWrap.For(laptop.Keys, Household, 2, HouseholdCrypto.NewKey(),
            [desktop.AsMember(), laptop.AsMember()]), CancellationToken.None)).Ok.ShouldBeTrue();
        var newbie = NewPc("Newbie");                                              // welcomed at 1 by the desktop, which knew D was removed
        newbie.Store.EnterHousehold(Household, 1, _key1);
        foreach (var member in new[] { desktop, laptop, newbie }) newbie.Household.SaveMember(member.AsMember());
        newbie.Members.Learn([new WireMember(gone.Id, null, null, AddedEpoch: 1, RemovedEpoch: 1)], desktop.Id, newbie.Id, NowMs);
        newbie.Household.Upsert([Row(newbie.Id, 3, 33, changed: NowMs + 1)]);
        _relay.Seed(Household, newbie.Keys);                                       // its add reaches the server after the new key, at 2

        (await newbie.RunAsync()).RowsOut.ShouldBe(0);
        _relay.Batches.ShouldNotContain(batch => batch.Device == newbie.Id);      // nothing under the key D holds
        (await newbie.RunAsync()).Problem.ShouldBeNull();

        newbie.Store.Epoch.ShouldBe(3);
        _relay.Sealed(Household, 3).ShouldBe([desktop.Id, laptop.Id, newbie.Id], ignoreOrder: true);
        var posted = _relay.Batches.Where(batch => batch.Device == newbie.Id).ToList();
        posted.ShouldNotBeEmpty();
        posted.ShouldAllBe(batch => batch.Epoch == 3);
    }

    [Fact]
    public async Task A_key_planted_for_a_pc_before_its_add_is_never_sealed_to_and_the_one_the_server_lists_takes_its_place()
    {
        var (b, m) = (NewPc("Laptop-B"), NewPc("Desktop-M"));
        InHousehold(b, m);
        var x = NewPc("Study-X");
        using var rogue = DeviceKeys.Create();
        b.Household.SaveMember(x.AsMember() with { DhKey = rogue.DhPublic });      // planted over the network before X's add
        _relay.Seed(Household, x.Keys);                                            // X's add: the server lists its own keys
        b.Sync.StartRotation(Household);

        (await b.RunAsync()).Problem.ShouldBeNull();
        _relay.Epoch(Household).ShouldBe(1);                                       // the new key waits rather than go to the planted one
        m.Household.SaveMember(x.AsMember());                                      // M knows X's own keys, and its list says so
        m.Household.Upsert([Row(m.Id, 1, 10, changed: NowMs + 1)]);
        await m.RunAsync();
        await b.RunAsync();
        await b.RunAsync();

        _relay.Epoch(Household).ShouldBe(2);
        var mine = await x.Client.GetKeyAsync(x.Keys, Household, 2, CancellationToken.None);
        KeyWrap.Open(x.Keys, b.Keys.DhPublic, mine.Value!.Body, Household, 2).ShouldBe(b.Store.KeyFor(2));
    }

    public void Dispose()
    {
        foreach (var pc in _pcs) pc.Dispose();
    }

    private Pc NewPc(string name)
    {
        var pc = new Pc(name, _relay, _clock);
        _pcs.Add(pc);
        return pc;
    }

    /// <summary>Every PC in the household at epoch 1, each introduced to the others, all on the server.</summary>
    private void InHousehold(params Pc[] pcs)
    {
        foreach (var pc in pcs)
        {
            pc.Store.EnterHousehold(Household, 1, _key1);
            pc.Store.RelayConfirmed = true;
            pc.Store.SnapshotEpoch = 1;
            pc.Store.SnapshotAt = NowMs;
            foreach (var member in pcs) pc.Household.SaveMember(member.AsMember());
        }
        _relay.Seed(Household, [.. pcs.Select(pc => pc.Keys)]);
    }

    private static long Hour(int hour) => Now.AddDays(-1).AddHours(hour).ToUnixTimeMilliseconds();

    private static HouseholdRow Row(string device, int hour, double energyWh, long changed) => new(
        device, Hour(hour), energyWh, 1, 1, 1, 1, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    private sealed class Pc : IDisposable
    {
        private readonly TestDatabase _database = new();

        public Pc(string name, FakeRelay relay, TimeProvider clock)
        {
            Name = name;
            Store = new HouseholdStore(new SettingsRepository(_database.Db), () => name);
            Keys = Store.DeviceKeys();
            Household = new HouseholdRepository(_database.Db);
            Client = new RelayClient(FakeRelay.Endpoint, clock, relay);
            Sync = new RelaySync(Store, Household, Client, clock, NullLogger.Instance);
            Members = new MemberBook(Store, Household);
        }

        public string Name { get; }

        public HouseholdStore Store { get; }

        public DeviceKeys Keys { get; }

        public string Id => Keys.DeviceId;

        public HouseholdRepository Household { get; }

        public RelayClient Client { get; }

        public RelaySync Sync { get; }

        public MemberBook Members { get; }

        public HouseholdMember AsMember() => new(Id, Name, ChassisKind.Desktop, Keys.SignPublic, Keys.DhPublic, NowMs, null, null);

        public Task<RelayRun> RunAsync() => Sync.RunAsync(Keys, Name, ChassisKind.Desktop, CancellationToken.None);

        public void Dispose()
        {
            Client.Dispose();
            Keys.Dispose();
            _database.Dispose();
        }
    }
}
