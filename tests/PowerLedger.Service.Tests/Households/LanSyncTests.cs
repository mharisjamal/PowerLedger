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
        await channel.SendAsync(LanMessages.Write(LanMessages.Hello("sync", ephPublic, claimed, "Desktop-7", ChassisKind.Desktop, "i1")));
        var theirs = Hello.Of(LanMessages.Read(await channel.ReceiveAsync())).ShouldNotBeNull();
        var shared = HouseholdCrypto.Agree(eph, theirs.Eph);
        using var cipher = FrameCipher.For(adder: true, shared, ephPublic, theirs.Eph);
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
        }

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
            var sync = new LanSync(Household, _clock);
            Listener = new LanListener(IPAddress.Loopback, async (channel, hello, cancel) =>
            {
                if (hello.Purpose == "sync") await sync.RespondAsync(channel, hello, Identity, cancel);
            }, NullLogger.Instance);
            Listener.Start();
        }

        public async Task<SyncOutcome> SyncWith(Pc other)
        {
            await using var channel = await LanConnector.ConnectAsync(IPAddress.Loopback, other.Listener!.Port, TimeSpan.FromSeconds(5));
            return await new LanSync(Household, _clock).SyncAsync(channel, Identity, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (Listener is not null) await Listener.DisposeAsync();
            Keys.Dispose();
            _database.Dispose();
        }
    }
}
