using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Relay;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Sync through the server (households design §5, §6): sealed batches up and down, what waits for the server, the
/// year for a new member, removals and new epochs, against the Worker's routes in memory.</summary>
public sealed class RelaySyncTests : IDisposable
{
    private const string Household = "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f";
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRelay _relay;
    private readonly byte[] _key = HouseholdCrypto.NewKey();
    private readonly RelayPc _desktop;
    private readonly RelayPc _laptop;

    public RelaySyncTests()
    {
        _relay = new FakeRelay(_clock);
        _desktop = new RelayPc("Desktop-7", ChassisKind.Desktop, _relay, _clock);
        _laptop = new RelayPc("Laptop-2", ChassisKind.Laptop, _relay, _clock);
        foreach (var pc in new[] { _desktop, _laptop })
        {
            pc.Store.EnterHousehold(Household, 1, _key);
            pc.Store.HistoryPosted = [_desktop.Id, _laptop.Id];
            foreach (var member in new[] { _desktop, _laptop }) pc.Household.SaveMember(member.AsMember());
        }
        _relay.Seed(Household, _desktop.Keys, _laptop.Keys);
    }

    [Fact]
    public async Task A_signed_request_carries_the_device_the_time_and_a_signature_over_the_method_path_time_and_body()
    {
        HttpRequestMessage? seen = null;
        byte[]? seenBody = null;
        _relay.Intercept = (request, body) =>
        {
            seen = request;
            seenBody = body;
            return null;
        };

        await _laptop.Client.MembersAsync(_laptop.Keys, Household, CancellationToken.None);

        seen.ShouldNotBeNull();
        seen.Headers.GetValues("X-PL-Device").Single().ShouldBe(_laptop.Id);
        var time = long.Parse(seen.Headers.GetValues("X-PL-Time").Single());
        time.ShouldBe(Now.ToUnixTimeSeconds());
        var signature = Wire.Decode(seen.Headers.GetValues("X-PL-Signature").Single()).ShouldNotBeNull();
        signature.Length.ShouldBe(64);
        HouseholdCrypto.Verify(_laptop.Keys.SignPublic, HouseholdCrypto.RequestToSign("GET", $"/v1/households/{Household}/members", time, seenBody!), signature)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Rows_go_up_sealed_and_come_down_on_the_other_member_with_its_name_and_kind()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100), Row(_desktop.Id, 1, 11, changed: 200)]);
        _laptop.Household.SaveMember(_desktop.AsMember() with { Name = "An old name" });

        var up = await _desktop.RunAsync();
        var down = await _laptop.RunAsync();

        (up.Problem, up.RowsOut, down.Problem, down.RowsIn).ShouldBe(((string?)null, 2, (string?)null, 2));
        _laptop.Household.Row(_desktop.Id, Hour(1)).ShouldNotBeNull().EnergyWh.ShouldBe(11);
        var desktop = _laptop.Household.Member(_desktop.Id).ShouldNotBeNull();
        (desktop.Name, desktop.Kind, desktop.LastSyncedMs).ShouldBe(("Desktop-7", ChassisKind.Desktop, (long?)200));
        var posted = _relay.Batches.ShouldHaveSingleItem();
        (posted.Device, posted.Epoch, posted.Seq).ShouldBe((_desktop.Id, 1, 1L));
        System.Text.Encoding.UTF8.GetString(posted.Body).ShouldNotContain("Desktop-7");

        (await _laptop.RunAsync()).RowsIn.ShouldBe(0);                        // the cursor moved past it
        (await _desktop.RunAsync()).RowsOut.ShouldBe(0);                      // and nothing new went up
        _desktop.Household.Upsert([Row(_desktop.Id, 1, 12, changed: 300)]);
        (await _desktop.RunAsync()).RowsOut.ShouldBe(1);
        (await _laptop.RunAsync()).RowsIn.ShouldBe(1);
        _laptop.Household.Row(_desktop.Id, Hour(1)).ShouldNotBeNull().EnergyWh.ShouldBe(12);
    }

    [Fact]
    public async Task What_the_server_has_to_hear_after_a_pairing_waits_until_it_answers()
    {
        var relay = new FakeRelay(_clock);
        using var desktop = new RelayPc("Desktop-7", ChassisKind.Desktop, relay, _clock);
        const string Fresh = "aaaa0000bbbb1111aaaa0000bbbb1111";
        desktop.Store.EnterHousehold(Fresh, 1, _key);
        desktop.Store.AddPending(new PendingOp(PendingOp.Create, Fresh));
        desktop.Store.AddPending(new PendingOp(PendingOp.Add, Fresh, Sign: Wire.Encode(_laptop.Keys.SignPublic), Dh: Wire.Encode(_laptop.Keys.DhPublic)));
        relay.Down = true;

        var offline = await desktop.RunAsync();

        offline.Problem.ShouldNotBeNull().ShouldStartWith("Couldn't reach the server to update the household");
        desktop.Store.Pending.Count.ShouldBe(2);
        desktop.Store.Problem.ShouldBe(offline.Problem);

        relay.Down = false;
        (await desktop.RunAsync()).Problem.ShouldBeNull();

        desktop.Store.Pending.ShouldBeEmpty();
        desktop.Store.Problem.ShouldBeNull();
        relay.Members(Fresh).Keys.ShouldBe([desktop.Id, _laptop.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_new_member_gets_each_members_year_of_rows_once_in_batches_of_at_most_1_MB()
    {
        var random = new Random(7);
        var year = Enumerable.Range(0, 24 * 390).Select(hour => new HouseholdRow(
            _desktop.Id, Now.AddHours(-hour - 1).ToUnixTimeMilliseconds(), random.NextDouble() * 100, random.NextDouble() * 50, random.NextDouble() * 30,
            random.NextDouble() * 10, random.NextDouble() * 10, random.NextDouble(), random.NextDouble(), random.NextDouble() * 3600,
            random.NextDouble() * 3600, random.NextDouble() * 600, random.NextDouble() * 3600, random.NextDouble() * 60, random.NextDouble() * 60,
            random.NextInt64(100_000), "EUR", ChangedMs: 1_000)).ToList();
        _desktop.Household.Upsert(year);
        _desktop.Store.PostedThrough = 1_000;                                   // all posted long ago
        _desktop.Store.HistoryPosted = [_desktop.Id];                           // and the laptop is new

        var first = await _desktop.RunAsync();
        var again = await _desktop.RunAsync();

        first.Problem.ShouldBeNull();
        var withinYear = year.Count(row => row.HourMs >= Now.AddMonths(-13).ToUnixTimeMilliseconds());
        first.RowsOut.ShouldBe(withinYear);
        _relay.Batches.Count.ShouldBeGreaterThan(1);
        again.RowsOut.ShouldBe(0);
        (await _laptop.RunAsync()).RowsIn.ShouldBe(withinYear);
        _laptop.Household.RowsBetween(_desktop.Id, 0, long.MaxValue).Count.ShouldBe(withinYear);
    }

    [Fact]
    public async Task A_batch_that_doesnt_open_is_passed_over_and_the_cursor_moves_on()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        _desktop.Store.EnterHousehold(Household, 1, HouseholdCrypto.NewKey());       // a key the laptop doesn't hold
        _desktop.Store.HistoryPosted = [_desktop.Id, _laptop.Id];
        await _desktop.RunAsync();

        var run = await _laptop.RunAsync();

        run.RowsIn.ShouldBe(0);
        run.Problem.ShouldBeNull();
        _laptop.Store.RelayCursor.ShouldBe(1);
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldBeNull();
    }

    [Fact]
    public async Task A_pc_not_yet_known_is_learned_from_its_batch_under_the_current_key_only()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        study.Store.EnterHousehold(Household, 1, _key);
        study.Store.HistoryPosted = [_desktop.Id, _laptop.Id, study.Id];
        study.Household.SaveMember(study.AsMember());
        study.Household.Upsert([Row(study.Id, 0, 7, changed: 500)]);
        _relay.Seed(Household, study.Keys);

        await study.RunAsync();
        await _laptop.RunAsync();

        var learned = _laptop.Household.Member(study.Id).ShouldNotBeNull();
        (learned.Name, learned.Kind).ShouldBe(("Study PC", ChassisKind.Desktop));
        learned.DhKey.ShouldBe(study.Keys.DhPublic);
        _laptop.Household.Row(study.Id, Hour(0)).ShouldNotBeNull().EnergyWh.ShouldBe(7);
    }

    [Fact]
    public async Task A_removed_member_is_marked_as_left_and_a_removed_pc_leaves_keeping_its_rows()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        await _desktop.RunAsync();
        await _laptop.RunAsync();                                              // the server has taken the laptop as a member
        _relay.Remove(Household, _laptop.Id);
        _clock.Advance(RelaySync.MembersEvery);

        var desktopRun = await _desktop.RunAsync();
        var laptopRun = await _laptop.RunAsync();

        desktopRun.Notices.ShouldBe(["Laptop-2 is no longer in the household."]);
        _desktop.Household.Member(_laptop.Id).ShouldNotBeNull().LeftMs.ShouldNotBeNull();
        laptopRun.Removed.ShouldBeTrue();
        laptopRun.Notices.ShouldBe(["This PC was removed from the household."]);
        (_laptop.Store.HouseholdId, _laptop.Store.CurrentKey).ShouldBe((null, null));
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldNotBeNull();
        _laptop.Household.Members().ShouldAllBe(member => member.LeftMs != null);
    }

    [Fact]
    public async Task A_pc_the_server_hasnt_taken_yet_waits_quietly()
    {
        using var newcomer = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        newcomer.Store.EnterHousehold(Household, 1, _key);
        newcomer.Household.SaveMember(newcomer.AsMember());

        var run = await newcomer.RunAsync();

        (run.Problem, run.Removed).ShouldBe(((string?)null, false));
        newcomer.Store.HouseholdId.ShouldBe(Household);
    }

    [Fact]
    public async Task A_batch_under_a_new_epoch_fetches_this_pcs_envelope_for_it()
    {
        var next = HouseholdCrypto.NewKey();
        var envelopes = KeyWrap.For(_desktop.Keys, Household, 2, next, [_laptop.AsMember()]);
        _desktop.Store.AddKey(2, next);
        _desktop.Store.AddPending(new PendingOp(PendingOp.Keys, Household, Epoch: 2, Envelopes: envelopes));
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);

        await _desktop.RunAsync();
        var run = await _laptop.RunAsync();

        _relay.Batches.ShouldHaveSingleItem().Epoch.ShouldBe(2);
        run.RowsIn.ShouldBe(1);
        _laptop.Store.Epoch.ShouldBe(2);
        _laptop.Store.CurrentKey.ShouldBe(next);
        _laptop.Store.KeyFor(1).ShouldBe(_key);
    }

    [Fact]
    public async Task Two_rotations_to_the_same_epoch_settle_on_the_one_the_server_took_first()
    {
        var mine = HouseholdCrypto.NewKey();
        var theirs = HouseholdCrypto.NewKey();
        await _laptop.Client.PostKeysAsync(_laptop.Keys, Household, 2, KeyWrap.For(_laptop.Keys, Household, 2, theirs, [_desktop.AsMember()]),
            CancellationToken.None);
        _desktop.Store.AddKey(2, mine);
        _desktop.Store.AddPending(new PendingOp(PendingOp.Keys, Household, Epoch: 2,
            Envelopes: KeyWrap.For(_desktop.Keys, Household, 2, mine, [_laptop.AsMember()])));

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.CurrentKey.ShouldBe(theirs);
        _desktop.Store.Pending.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _desktop.Dispose();
        _laptop.Dispose();
    }

    private static long Hour(int hour) => Now.AddDays(-1).AddHours(hour).ToUnixTimeMilliseconds();

    private static HouseholdRow Row(string device, int hour, double energyWh, long changed) => new(
        device, Hour(hour), energyWh, 1, 1, 1, 1, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    /// <summary>One PC with its own database, keys and relay sync, on a shared fake server.</summary>
    private sealed class RelayPc : IDisposable
    {
        private readonly TestDatabase _database = new();

        public RelayPc(string name, ChassisKind kind, FakeRelay relay, TimeProvider clock)
        {
            Name = name;
            Kind = kind;
            Store = new HouseholdStore(new SettingsRepository(_database.Db), () => name);
            Keys = Store.DeviceKeys();
            Household = new HouseholdRepository(_database.Db);
            Client = new RelayClient(FakeRelay.Endpoint, clock, relay);
            Sync = new RelaySync(Store, Household, Client, clock, NullLogger.Instance);
        }

        public string Name { get; }

        public ChassisKind Kind { get; }

        public HouseholdStore Store { get; }

        public DeviceKeys Keys { get; }

        public string Id => Keys.DeviceId;

        public HouseholdRepository Household { get; }

        public RelayClient Client { get; }

        public RelaySync Sync { get; }

        public HouseholdMember AsMember() => new(Id, Name, Kind, Keys.SignPublic, Keys.DhPublic, 0, null, null);

        public Task<RelayRun> RunAsync() => Sync.RunAsync(Keys, Name, Kind, CancellationToken.None);

        public void Dispose()
        {
            Client.Dispose();
            Keys.Dispose();
            _database.Dispose();
        }
    }
}
