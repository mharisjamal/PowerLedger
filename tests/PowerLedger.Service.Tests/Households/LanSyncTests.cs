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

/// <summary>Sync on the same network (households design §5): members prove their keys, swap what each has, and send what
/// the other lacks, over the listener's TCP port on loopback.</summary>
public sealed class LanSyncTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly Pc _desktop;
    private readonly Pc _laptop;

    public LanSyncTests()
    {
        _desktop = new Pc("Desktop-7", ChassisKind.Desktop, _clock);
        _laptop = new Pc("Laptop-2", ChassisKind.Laptop, _clock);
    }

    public async Task InitializeAsync()
    {
        foreach (var pc in new[] { _desktop, _laptop })
        {
            foreach (var member in new[] { _desktop, _laptop }) pc.Household.SaveMember(member.AsMember(Now));
            pc.Start();
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Two_members_exchange_the_rows_each_lacks_both_ways()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100), Row(_desktop.Id, 1, 11, changed: 200)]);
        _laptop.Household.Upsert([Row(_laptop.Id, 0, 20, changed: 150), Row(_desktop.Id, 0, 10, changed: 100)]);

        var outcome = await _desktop.SyncWith(_laptop);

        outcome.Ok.ShouldBeTrue();
        outcome.PeerId.ShouldBe(_laptop.Id);
        (outcome.RowsOut, outcome.RowsIn).ShouldBe((1, 1));
        _laptop.Household.Row(_desktop.Id, Hour(1)).ShouldNotBeNull().EnergyWh.ShouldBe(11);
        _desktop.Household.Row(_laptop.Id, Hour(0)).ShouldNotBeNull().EnergyWh.ShouldBe(20);
        _desktop.Household.Member(_laptop.Id).ShouldNotBeNull().LastSyncedMs.ShouldBe(Now.ToUnixTimeMilliseconds());
        await WaitFor.True(() => _laptop.Household.Member(_desktop.Id)?.LastSyncedMs == Now.ToUnixTimeMilliseconds());

        (await _desktop.SyncWith(_laptop)).ShouldSatisfyAllConditions(again => again.RowsIn.ShouldBe(0), again => again.RowsOut.ShouldBe(0));
    }

    [Fact]
    public async Task A_later_change_of_an_hour_replaces_the_one_kept_and_many_rows_go_in_several_frames()
    {
        _desktop.Household.Upsert([.. Enumerable.Range(0, LanSync.RowsPerFrame + 5).Select(hour => Row(_desktop.Id, hour, 1, changed: 100))]);
        await _desktop.SyncWith(_laptop);
        _desktop.Household.Upsert([Row(_desktop.Id, 3, 99, changed: 300)]);

        var outcome = await _laptop.SyncWith(_desktop);

        outcome.RowsIn.ShouldBe(1);
        _laptop.Household.Row(_desktop.Id, Hour(3)).ShouldNotBeNull().EnergyWh.ShouldBe(99);
        _laptop.Household.RowsBetween(_desktop.Id, 0, long.MaxValue).Count.ShouldBe(LanSync.RowsPerFrame + 5);
    }

    [Fact]
    public async Task A_sync_cut_short_goes_on_next_time_from_the_last_row_that_came_even_among_rows_changed_at_once()
    {
        _desktop.Household.Upsert([.. Enumerable.Range(0, LanSync.RowsPerFrame + 5).Select(hour => Row(_desktop.Id, hour, 1, changed: 100))]);

        (await _laptop.SyncWith(_desktop, cutAfter: 4)).Ok.ShouldBeFalse();     // hello, prove, have, the first rows; then the Wi-Fi drops
        _laptop.Household.RowsBetween(_desktop.Id, 0, long.MaxValue).Count.ShouldBe(LanSync.RowsPerFrame);

        var again = await _laptop.SyncWith(_desktop);

        again.RowsIn.ShouldBe(5);
        _laptop.Household.RowsBetween(_desktop.Id, 0, long.MaxValue).Count.ShouldBe(LanSync.RowsPerFrame + 5);
    }

    [Fact]
    public async Task A_member_learns_of_a_new_member_the_other_knows_and_of_its_rows()
    {
        using var newcomer = DeviceKeys.Create();
        var entry = new HouseholdMember(newcomer.DeviceId, "Study PC", ChassisKind.Desktop, newcomer.SignPublic, newcomer.DhPublic, 0, null, null);
        _desktop.Household.SaveMember(entry);
        _desktop.Household.Upsert([Row(newcomer.DeviceId, 0, 5, changed: 400)]);

        await _laptop.SyncWith(_desktop);

        var learned = _laptop.Household.Member(newcomer.DeviceId).ShouldNotBeNull();
        (learned.Name, learned.Kind, learned.LeftMs).ShouldBe(("Study PC", ChassisKind.Desktop, (long?)null));
        learned.SignKey.ShouldBe(newcomer.SignPublic);
        _laptop.Household.Row(newcomer.DeviceId, Hour(0)).ShouldNotBeNull().EnergyWh.ShouldBe(5);
        _laptop.Household.Member(newcomer.DeviceId).ShouldNotBeNull().LastSyncedMs.ShouldBe(400);
    }

    [Fact]
    public async Task A_pc_removed_here_isnt_taken_back_from_a_member_that_hasnt_heard_and_that_member_learns_of_the_removal()
    {
        using var study = DeviceKeys.Create();
        var entry = new HouseholdMember(study.DeviceId, "Study PC", ChassisKind.Desktop, study.SignPublic, study.DhPublic, 0, null, null);
        _desktop.Household.SaveMember(entry);
        _laptop.Household.SaveMember(entry);                                     // the laptop hasn't heard yet
        var removed = Now.ToUnixTimeMilliseconds() - 60_000;
        _desktop.Members.Remove(study.DeviceId, removed).ShouldBeTrue();
        _desktop.Members.ForgetRows(study.DeviceId);                              // and its rows went too

        (await _desktop.SyncWith(_laptop)).Ok.ShouldBeTrue();
        await WaitFor.True(() => _laptop.Household.Member(study.DeviceId)?.LeftMs is not null);

        _desktop.Household.Member(study.DeviceId).ShouldBeNull();                 // not back as a current member
        _desktop.Store.Tombstones.ShouldContainKey(study.DeviceId);
        _laptop.Household.Member(study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBe(removed);
        (await _laptop.SyncWith(_desktop)).ShouldSatisfyAllConditions(
            again => again.Ok.ShouldBeTrue(), again => again.Removed.ShouldNotBeNull().ShouldBeEmpty());
    }

    [Fact]
    public async Task A_removed_pc_added_again_after_its_removal_comes_back_and_its_removal_is_learned_with_the_time()
    {
        using var study = DeviceKeys.Create();
        var entry = new HouseholdMember(study.DeviceId, "Study PC", ChassisKind.Desktop, study.SignPublic, study.DhPublic, 0, null, null);
        _laptop.Household.SaveMember(entry);
        _laptop.Members.Remove(study.DeviceId, 1_000);
        _desktop.Household.SaveMember(entry with { AddedMs = 2_000 });              // the desktop's user added it again later

        var outcome = await _laptop.SyncWith(_desktop);

        outcome.Ok.ShouldBeTrue();
        _laptop.Household.Member(study.DeviceId).ShouldNotBeNull().LeftMs.ShouldBeNull();
        _laptop.Store.Tombstones.ShouldNotContainKey(study.DeviceId);
    }

    [Fact]
    public async Task A_pc_that_isnt_a_member_is_refused_and_one_claiming_a_members_key_fails_its_prove()
    {
        var stranger = new Pc("Stranger", ChassisKind.Laptop, _clock);
        foreach (var member in new[] { _desktop, _laptop, stranger }) stranger.Household.SaveMember(member.AsMember(Now));
        _laptop.Household.Upsert([Row(_laptop.Id, 0, 20, changed: 150)]);

        var outcome = await stranger.SyncWith(_laptop);

        outcome.Ok.ShouldBeFalse();
        stranger.Household.Row(_laptop.Id, Hour(0)).ShouldBeNull();

        // Someone with its own keys who claims the desktop's signing key can't sign as the desktop.
        (await ClaimToBe(_desktop.Keys, signingWith: stranger.Keys, _laptop.Listener!.Port)).ShouldBeFalse();
        await stranger.DisposeAsync();
    }

    [Fact]
    public async Task A_sync_that_came_in_keeps_nothing_once_this_pc_has_left_the_household_it_began_in()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        _laptop.StillIn = false;                                                   // the laptop left while the sync was on its way

        (await _desktop.SyncWith(_laptop)).Ok.ShouldBeFalse();

        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldBeNull();
    }

    [Fact]
    public async Task At_most_two_connections_at_once_are_served_from_one_address()
    {
        var port = _laptop.Listener!.Port;
        await using var first = await LanConnector.ConnectAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5));
        await using var second = await LanConnector.ConnectAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        await using var third = await LanConnector.ConnectAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5));

        (await third.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeNull();   // turned away at once
        var waiting = first.ReceiveAsync();
        (await Task.WhenAny(waiting, Task.Delay(300))).ShouldNotBe(waiting);                // still served, waiting for its hello
    }

    [Fact]
    public async Task A_member_that_has_left_is_refused()
    {
        _laptop.Household.MarkLeft(_desktop.Id, Now.ToUnixTimeMilliseconds());
        _laptop.Household.Upsert([Row(_laptop.Id, 0, 20, changed: 150)]);

        (await _desktop.SyncWith(_laptop)).Ok.ShouldBeFalse();

        _desktop.Household.Row(_laptop.Id, Hour(0)).ShouldBeNull();
    }

    public async Task DisposeAsync()
    {
        await _desktop.DisposeAsync();
        await _laptop.DisposeAsync();
    }

    private static long Hour(int hour) => Now.AddDays(-1).AddHours(hour).ToUnixTimeMilliseconds();

    private static HouseholdRow Row(string device, int hour, double energyWh, long changed) => new(
        device, Hour(hour), energyWh, 1, 1, 1, 1, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    /// <summary>A hand-made sync that presents <paramref name="claimed"/>'s keys but signs its prove with another key.</summary>
    /// <returns>True when the other side proved itself back, as it would to a member.</returns>
    private static async Task<bool> ClaimToBe(DeviceKeys claimed, DeviceKeys signingWith, int port)
    {
        await using var channel = await LanConnector.ConnectAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5));
        using var eph = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var ephPublic = eph.ExportSubjectPublicKeyInfo();
        var mine = LanMessages.Write(LanMessages.Hello("sync", ephPublic, claimed, "Desktop-7", ChassisKind.Desktop, "i1"));
        await channel.SendAsync(mine);
        var theirHello = (await channel.ReceiveAsync()).ShouldNotBeNull();
        var theirs = Hello.Of(LanMessages.Read(theirHello)).ShouldNotBeNull();
        var shared = HouseholdCrypto.Agree(eph, theirs.Eph);
        using var cipher = FrameCipher.For(adder: true, shared, HouseholdCrypto.Transcript(mine, theirHello));
        var signature = HouseholdCrypto.SignData(signingWith.Sign, [.. "sync"u8, .. ephPublic, .. theirs.Eph]);
        await channel.SendAsync(cipher.Seal(LanMessages.Write(new LanMessage { Type = "prove", Sig = Wire.Encode(signature) })));
        try
        {
            return await channel.ReceiveAsync() is { } next && LanMessages.Read(cipher.Open(next))?.Type == "prove";
        }
        catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>A connection that breaks once it has handed over a number of frames.</summary>
    private sealed class CutAfter(IFrameChannel inner, int frames) : IFrameChannel
    {
        private int _count;

        public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancel = default) => inner.SendAsync(frame, cancel);

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancel = default) => ++_count > frames ? null : await inner.ReceiveAsync(cancel);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>One PC: its database, its keys and its listener on loopback, answering sync hellos.</summary>
    private sealed class Pc : IAsyncDisposable
    {
        private readonly TestDatabase _database = new();
        private readonly FakeTimeProvider _clock;

        public Pc(string name, ChassisKind kind, FakeTimeProvider clock)
        {
            _clock = clock;
            Name = name;
            Kind = kind;
            Household = new HouseholdRepository(_database.Db);
            Store = new HouseholdStore(new SettingsRepository(_database.Db), () => name);
            Members = new MemberBook(Store, Household);
        }

        public HouseholdStore Store { get; }

        public MemberBook Members { get; }

        /// <summary>Whether this PC is still in the household, as a sync that came in asks before keeping anything.</summary>
        public bool StillIn { get; set; } = true;

        public string Name { get; }

        public ChassisKind Kind { get; }

        public DeviceKeys Keys { get; } = DeviceKeys.Create();

        public string Id => Keys.DeviceId;

        public HouseholdRepository Household { get; }

        public LanListener? Listener { get; private set; }

        public PairingIdentity Identity => new(Keys, Name, Kind, Id[..16]);

        public HouseholdMember AsMember(DateTimeOffset at) => new(Id, Name, Kind, Keys.SignPublic, Keys.DhPublic, at.ToUnixTimeMilliseconds(), null, null);

        public void Start()
        {
            var sync = new LanSync(Household, Members, _clock);
            Listener = new LanListener(IPAddress.Loopback, async (call, cancel) =>
            {
                if (call.Message.Purpose == "sync") await sync.RespondAsync(call.Channel, call.Hello, Identity, cancel, () => StillIn);
            }, NullLogger.Instance);
            Listener.Start();
        }

        /// <param name="cutAfter">The connection breaks once this side has received so many frames.</param>
        public async Task<SyncOutcome> SyncWith(Pc other, int? cutAfter = null)
        {
            await using var channel = await LanConnector.ConnectAsync(IPAddress.Loopback, other.Listener!.Port, TimeSpan.FromSeconds(5));
            return await new LanSync(Household, Members, _clock).SyncAsync(cutAfter is { } frames ? new CutAfter(channel, frames) : channel, Identity, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (Listener is not null) await Listener.DisposeAsync();
            Keys.Dispose();
            _database.Dispose();
        }
    }
}
