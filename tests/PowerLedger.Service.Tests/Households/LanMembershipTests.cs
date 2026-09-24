using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Who a PC syncs with on the network (plan 0.10), with three members: a removed PC can't put itself back with its
/// own list, and no member's rows are taken for this PC's own device.</summary>
public sealed class LanMembershipTests : IAsyncLifetime
{
    private const string Household = "5e1f0c2a9b8d4e3f5e1f0c2a9b8d4e3f";
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly long NowMs = Now.ToUnixTimeMilliseconds();

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly byte[] _key1 = HouseholdCrypto.NewKey();
    private readonly LanPc _a = new("Desktop-A");
    private readonly LanPc _b = new("Laptop-B");
    private readonly LanPc _r = new("Rogue-R");

    public Task InitializeAsync()
    {
        var all = new[] { _a, _b, _r };
        foreach (var pc in all)
        {
            pc.Store.EnterHousehold(Household, 1, _key1);
            foreach (var member in all) pc.Household.SaveMember(member.AsMember());
            pc.Start(_clock);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_removed_pc_cant_put_itself_back_with_its_own_list_nor_read_rows_made_after_its_removal()
    {
        _a.Members.Remove(_r.Id, NowMs).ShouldBeTrue();                           // A removes R at epoch 1
        (await _r.SyncWith(_b)).Ok.ShouldBeTrue();                                // R gets to B before B hears
        _b.Members.Learn([new WireMember(_r.Id, "Rogue-R", "desktop", Wire.Encode(_r.Keys.SignPublic), Wire.Encode(_r.Keys.DhPublic), AddedEpoch: 2)],
            _r.Id, _b.Id, NowMs);                                                  // its own entry, claiming it was added again at 2

        _b.Members.TakeServerList([_a.Listed(1), _b.Listed(1), _r.Listed(1, removedAt: 1)], _b.Id, NowMs);   // the server: removed at 1
        (await _a.SyncWith(_b)).Ok.ShouldBeTrue();                                // and A's list: removed at 1

        _b.Members.Current(_r.Id).ShouldBeNull();
        _b.Members.MaySeal(_r.Id, 3).ShouldBeFalse();
        await WaitFor.True(() => _a.Members.ShownRemoved(_r.Id));
        _a.Members.Current(_r.Id).ShouldBeNull();                                 // the remover never takes it back from B's list
        _a.Household.Upsert([Row(_a.Id, 5, 99, changed: NowMs + 10)]);            // A's row made after the removal
        (await _r.SyncWith(_a)).Ok.ShouldBeFalse();
        (await _r.SyncWith(_b)).Ok.ShouldBeFalse();
        _r.Household.Row(_a.Id, Hour(5)).ShouldBeNull();

        _clock.Advance(TimeSpan.FromDays(3));
        _b.Members.TakeServerList([_a.Listed(1), _b.Listed(1), _r.Listed(1, removedAt: 1)], _b.Id, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        _b.Members.Current(_r.Id).ShouldBeNull();                                  // and it stays out
    }

    [Fact]
    public async Task A_members_rows_for_this_pcs_own_device_are_never_taken()
    {
        _b.Household.Upsert([Row(_b.Id, 1, 10, changed: 100)]);
        _r.Household.Upsert([Row(_b.Id, 1, 666, changed: 500)]);                  // R's forged row for B's own device

        (await _r.SyncWith(_b)).Ok.ShouldBeTrue();

        _b.Household.Row(_b.Id, Hour(1)).ShouldNotBeNull().EnergyWh.ShouldBe(10);
        _b.Household.ChangedAfter(_b.Id, 100).ShouldBeEmpty();                    // nothing new goes out as B's own
    }

    public async Task DisposeAsync()
    {
        foreach (var pc in new[] { _a, _b, _r }) await pc.DisposeAsync();
    }

    internal static long Hour(int hour) => Now.AddDays(-1).AddHours(hour).ToUnixTimeMilliseconds();

    internal static HouseholdRow Row(string device, int hour, double energyWh, long changed) => new(
        device, Hour(hour), energyWh, 1, 1, 1, 1, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    /// <summary>One PC with its own database, keys and listener on loopback, answering sync hellos.</summary>
    private sealed class LanPc : IAsyncDisposable
    {
        private readonly TestDatabase _database = new();
        private readonly string _name;
        private TimeProvider _clock = TimeProvider.System;

        public LanPc(string name)
        {
            _name = name;
            Household = new HouseholdRepository(_database.Db);
            Store = new HouseholdStore(new SettingsRepository(_database.Db), () => name);
            Members = new MemberBook(Store, Household);
        }

        public HouseholdStore Store { get; }

        public HouseholdRepository Household { get; }

        public MemberBook Members { get; }

        public DeviceKeys Keys { get; } = DeviceKeys.Create();

        public string Id => Keys.DeviceId;

        public LanListener? Listener { get; private set; }

        private PairingIdentity Identity => new(Keys, _name, ChassisKind.Desktop, Id[..16]);

        public HouseholdMember AsMember() => new(Id, _name, ChassisKind.Desktop, Keys.SignPublic, Keys.DhPublic, NowMs, null, null);

        public PowerLedger.Service.Households.Relay.ServerMember Listed(int added, int? removedAt = null) =>
            new(Id, Wire.Encode(Keys.SignPublic), Wire.Encode(Keys.DhPublic), NowMs, removedAt is null ? null : NowMs, added, removedAt);

        public void Start(TimeProvider clock)
        {
            _clock = clock;
            var sync = new LanSync(Household, Members, clock);
            Listener = new LanListener(IPAddress.Loopback, async (call, cancel) =>
            {
                if (call.Message.Purpose == "sync") await sync.RespondAsync(call.Channel, call.Hello, Identity, cancel, () => true);
            }, NullLogger.Instance);
            Listener.Start();
        }

        public async Task<SyncOutcome> SyncWith(LanPc other)
        {
            await using var channel = await LanConnector.ConnectAsync(IPAddress.Loopback, other.Listener!.Port, TimeSpan.FromSeconds(5));
            return await new LanSync(Household, Members, _clock).SyncAsync(channel, Identity, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (Listener is not null) await Listener.DisposeAsync();
            Keys.Dispose();
            _database.Dispose();
        }
    }
}
