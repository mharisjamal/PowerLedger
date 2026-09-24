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
            AsIfSnapshotJustWent(pc);
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
        desktop.Store.AddPending(new PendingOp(PendingOp.Add, Fresh, Sign: Wire.Encode(_laptop.Keys.SignPublic), Dh: Wire.Encode(_laptop.Keys.DhPublic),
            Proof: Wire.Encode(Wire.SignJoin(_laptop.Keys, Fresh))));
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
    public async Task A_snapshot_of_this_pcs_year_goes_in_batches_of_at_most_1_MB_and_a_new_member_reads_it_all()
    {
        var random = new Random(7);
        var year = Enumerable.Range(0, 24 * 390).Select(hour => new HouseholdRow(
            _desktop.Id, Now.AddHours(-hour - 1).ToUnixTimeMilliseconds(), random.NextDouble() * 100, random.NextDouble() * 50, random.NextDouble() * 30,
            random.NextDouble() * 10, random.NextDouble() * 10, random.NextDouble(), random.NextDouble(), random.NextDouble() * 3600,
            random.NextDouble() * 3600, random.NextDouble() * 600, random.NextDouble() * 3600, random.NextDouble() * 60, random.NextDouble() * 60,
            random.NextInt64(100_000), "EUR", ChangedMs: 1_000)).ToList();
        _desktop.Household.Upsert(year);
        _desktop.Store.PostedThrough = 1_000;                                   // all posted long ago
        _desktop.Store.SnapshotAt = Now.AddDays(-1).ToUnixTimeMilliseconds();   // the last snapshot a day ago,
        _desktop.Store.SnapshotWanted = true;                                   // and the laptop new since

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
    public async Task Every_pc_posts_all_its_rows_again_every_30_days_so_rows_whose_batches_expired_reach_a_pc_that_was_away()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        await _desktop.RunAsync();
        _clock.Advance(TimeSpan.FromDays(29));
        _desktop.Household.Upsert([Row(_desktop.Id, 1, 11, changed: 200)]);
        (await _desktop.RunAsync()).RowsOut.ShouldBe(1);                          // only what changed, within the 30 days
        _clock.Advance(TimeSpan.FromDays(2));
        var beforeSnapshot = _relay.LastSeq(Household);

        (await _desktop.RunAsync()).RowsOut.ShouldBe(2);                          // 31 days on: everything again
        _relay.DropBatches(Household, beforeSnapshot);                             // the older batches expire on the server

        (await _laptop.RunAsync()).RowsIn.ShouldBe(2);
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldNotBeNull().EnergyWh.ShouldBe(10);
    }

    [Fact]
    public async Task A_snapshot_goes_at_once_under_a_new_key_but_after_a_new_member_at_most_once_a_day()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100), Row(_desktop.Id, 1, 11, changed: 100)]);
        _desktop.Store.PostedThrough = 100;
        using var study = DeviceKeys.Create();
        _desktop.Members.Introduce(new MemberInfo(study.DeviceId, "Study PC", ChassisKind.Desktop, study.SignPublic, study.DhPublic),
            Now.ToUnixTimeMilliseconds());                                          // a new member, an hour after the last snapshot
        _clock.Advance(TimeSpan.FromHours(1));

        (await _desktop.RunAsync()).RowsOut.ShouldBe(0);                          // not twice in a day
        _clock.Advance(TimeSpan.FromDays(1));
        (await _desktop.RunAsync()).RowsOut.ShouldBe(2);                          // a day on, for it
        (await _desktop.RunAsync()).RowsOut.ShouldBe(0);

        _desktop.Sync.StartRotation(Household);
        (await _desktop.RunAsync()).RowsOut.ShouldBe(2);                          // under the new key at once
        _relay.Batches.Last().Epoch.ShouldBe(2);
    }

    [Fact]
    public async Task A_year_cut_short_by_the_servers_daily_limit_goes_on_from_the_last_batch_that_went()
    {
        var random = new Random(11);
        var year = Enumerable.Range(0, 24 * 300).Select(hour => new HouseholdRow(
            _desktop.Id, Now.AddHours(-hour - 1).ToUnixTimeMilliseconds(), random.NextDouble() * 100, random.NextDouble() * 50, random.NextDouble() * 30,
            random.NextDouble() * 10, random.NextDouble() * 10, random.NextDouble(), random.NextDouble(), random.NextDouble() * 3600,
            random.NextDouble() * 3600, random.NextDouble() * 600, random.NextDouble() * 3600, random.NextDouble() * 60, random.NextDouble() * 60,
            random.NextInt64(100_000), "EUR", ChangedMs: 5_000)).ToList();
        _desktop.Household.Upsert(year);                                         // a year built at once, as on joining
        var posts = 0;
        _relay.Intercept = (request, _) => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/batches", StringComparison.Ordinal)
            && ++posts == 2 ? FakeRelay.Error(429, "Too many batches from this PC today.") : null;

        var cut = await _desktop.RunAsync();
        _relay.Intercept = null;
        var rest = await _desktop.RunAsync();

        cut.Problem.ShouldNotBeNull().ShouldContain("too much today");
        cut.RowsOut.ShouldBeGreaterThan(0);
        rest.Problem.ShouldBeNull();
        (cut.RowsOut + rest.RowsOut).ShouldBe(year.Count);                        // nothing went twice
        (await _desktop.RunAsync()).RowsOut.ShouldBe(0);
        (await _laptop.RunAsync()).RowsIn.ShouldBe(year.Count);
    }

    [Fact]
    public async Task After_the_clock_goes_back_the_hours_built_since_still_go_up()
    {
        _desktop.Aggregates.UpsertHour(Aggregate(Now.AddHours(-3)));
        _desktop.Rows.Build(_desktop.Id, Now.AddDays(-1), Now.AddHours(2));    // built while the clock was 2 h fast
        (await _desktop.RunAsync()).RowsOut.ShouldBe(1);

        _desktop.Aggregates.UpsertHour(Aggregate(Now.AddHours(-2)));
        _desktop.Rows.Build(_desktop.Id, Now.AddDays(-1), Now);                  // a new hour, once the clock was put right

        (await _desktop.RunAsync()).RowsOut.ShouldBe(1);
        (await _laptop.RunAsync()).RowsIn.ShouldBe(2);
    }

    [Fact]
    public async Task A_batch_that_doesnt_open_is_passed_over_and_the_cursor_moves_on()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        _desktop.Store.EnterHousehold(Household, 1, HouseholdCrypto.NewKey());       // a key the laptop doesn't hold
        AsIfSnapshotJustWent(_desktop);
        await _desktop.RunAsync();

        var run = await _laptop.RunAsync();

        run.RowsIn.ShouldBe(0);
        run.Problem.ShouldBeNull();
        _laptop.Store.RelayCursor.ShouldBe(1);
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldBeNull();
    }

    [Fact]
    public async Task A_pc_not_yet_known_waits_until_a_members_signed_list_introduces_it_even_when_its_batch_comes_first()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        study.Store.EnterHousehold(Household, 1, _key);
        AsIfSnapshotJustWent(study);
        study.Household.SaveMember(study.AsMember());
        study.Household.Upsert([Row(study.Id, 0, 7, changed: 500)]);
        _relay.Seed(Household, study.Keys);

        await study.RunAsync();                                                   // its batch comes first, and alone introduces nobody
        (await _laptop.RunAsync()).RowsIn.ShouldBe(0);
        _laptop.Household.Member(study.Id).ShouldBeNull();
        _laptop.Store.RelayCursor.ShouldBe(0);                                    // held, to be read again

        _desktop.Household.SaveMember(study.AsMember() with { AddedMs = 400 });    // the desktop added it
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 600)]);
        await _desktop.RunAsync();
        var run = await _laptop.RunAsync();

        run.RowsIn.ShouldBe(2);
        var learned = _laptop.Household.Member(study.Id).ShouldNotBeNull();
        (learned.Name, learned.Kind, learned.AddedMs).ShouldBe(("Study PC", ChassisKind.Desktop, 400L));
        learned.DhKey.ShouldBe(study.Keys.DhPublic);
        _laptop.Household.Row(study.Id, Hour(0)).ShouldNotBeNull().EnergyWh.ShouldBe(7);
        _laptop.Store.RelayCursor.ShouldBe(_relay.Batches.Count);                  // past them all now
    }

    [Fact]
    public async Task A_batch_not_signed_by_the_member_it_names_is_passed_over_though_it_opens_under_the_household_key()
    {
        using var removed = DeviceKeys.Create();                                  // it had the key, and was removed
        var forged = HouseholdJson.Bytes(new BatchPlain(1, new WireMember(_desktop.Id, "Desktop-7", "desktop"),
            [Wire.Row(Row(_desktop.Id, 0, 99_999, changed: 500))]), HouseholdJson.Default.BatchPlain);
        var sealedBody = HouseholdCrypto.Seal(_key, PowerLedger.Service.Sharing.SharingClient.Gzip(forged), HouseholdCrypto.BatchAad(Household, _desktop.Id, 1, 7));
        var sig = HouseholdCrypto.SignData(removed.Sign, HouseholdCrypto.BatchToSign(HouseholdCrypto.BatchAad(Household, _desktop.Id, 1, 7), sealedBody));
        _relay.Intercept = (request, _) => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/batches", StringComparison.Ordinal)
            ? FakeRelay.Json(new System.Text.Json.Nodes.JsonObject
            {
                ["items"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["seq"] = 7, ["device"] = _desktop.Id, ["epoch"] = 1, ["body"] = Wire.Encode(sealedBody), ["sig"] = Wire.Encode(sig),
                }),
                ["next"] = 1,
                ["more"] = false,
            })
            : null;

        var run = await _laptop.RunAsync();

        run.RowsIn.ShouldBe(0);
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldBeNull();
        _laptop.Store.RelayCursor.ShouldBe(1);                                    // passed over, not waited on
    }

    [Fact]
    public async Task Every_batch_goes_with_its_senders_signature_and_a_change_time_far_ahead_is_taken_as_a_day_from_now()
    {
        var farAhead = Now.AddYears(1).ToUnixTimeMilliseconds();
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: farAhead)]);

        await _desktop.RunAsync();
        await _laptop.RunAsync();

        var posted = _relay.Batches.ShouldHaveSingleItem();
        var aad = HouseholdCrypto.BatchAad(Household, _desktop.Id, posted.Epoch, posted.Seq);
        HouseholdCrypto.Verify(_desktop.Keys.SignPublic, HouseholdCrypto.BatchToSign(aad, posted.Body), posted.Sig).ShouldBeTrue();
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldNotBeNull().ChangedMs.ShouldBe(Now.AddDays(1).ToUnixTimeMilliseconds());
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

        for (var quiet = 1; quiet < RelaySync.QuietRuns; quiet++)
        {
            var run = await newcomer.RunAsync();
            (run.Problem, run.Removed).ShouldBe(((string?)null, false));
        }

        (await newcomer.RunAsync()).Problem.ShouldBe(RelaySync.NotAddedYet);        // after an hour, the wait shows
        newcomer.Store.HouseholdId.ShouldBe(Household);
    }

    [Fact]
    public async Task Before_the_server_takes_it_again_a_pc_added_back_after_a_removal_waits_instead_of_leaving()
    {
        _relay.Remove(Household, _laptop.Id);                                     // removed, which it hadn't heard of
        _laptop.Store.RelayConfirmed = false;                                     // then paired in again: the add is still on its way

        var run = await _laptop.RunAsync();

        (run.Problem, run.Removed).ShouldBe(((string?)null, false));
        _laptop.Store.HouseholdId.ShouldBe(Household);
        _laptop.Store.CurrentKey.ShouldBe(_key);
    }

    [Fact]
    public async Task A_clock_far_off_the_servers_shows_as_the_problem_at_once()
    {
        using var skewed = new RelayPc("Laptop-2", ChassisKind.Laptop, _relay, new FakeTimeProvider(Now.AddMinutes(12)));
        skewed.Store.EnterHousehold(Household, 1, _key);
        skewed.Household.SaveMember(skewed.AsMember());
        _relay.Seed(Household, skewed.Keys);

        var run = await skewed.RunAsync();

        run.Problem.ShouldBe("This PC's clock is 12 minutes ahead, so the server refuses its requests. Set the clock right.");
        skewed.Client.Skew.ShouldBe(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public async Task A_batch_under_a_new_epoch_fetches_this_pcs_envelope_for_it()
    {
        _desktop.Sync.StartRotation(Household);
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);

        await _desktop.RunAsync();
        var run = await _laptop.RunAsync();

        _desktop.Store.Epoch.ShouldBe(2);
        _relay.Batches.ShouldNotBeEmpty();
        _relay.Batches.ShouldAllBe(batch => batch.Epoch == 2);                   // the new row, and every row again under the new key
        run.RowsIn.ShouldBe(1);
        _laptop.Store.Epoch.ShouldBe(2);
        _laptop.Store.CurrentKey.ShouldBe(_desktop.Store.CurrentKey);
        _laptop.Store.KeyFor(1).ShouldBe(_key);
    }

    [Fact]
    public async Task An_old_households_request_the_server_doesnt_take_from_this_pc_is_dropped_and_holds_nothing_up()
    {
        const string Old = "aaaa0000bbbb1111aaaa0000bbbb1111";                    // never on the server: this PC left it before it was told
        using var study = DeviceKeys.Create();
        _desktop.Store.AddPending(new PendingOp(PendingOp.Remove, Old, Device: _desktop.Id));
        _desktop.Store.AddPending(new PendingOp(PendingOp.Add, Household, Sign: Wire.Encode(study.SignPublic), Dh: Wire.Encode(study.DhPublic),
            Proof: Wire.Encode(Wire.SignJoin(study, Household))));

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.Pending.ShouldBeEmpty();
        _relay.Members(Household).Keys.ShouldContain(study.DeviceId);
    }

    [Fact]
    public async Task An_old_households_request_that_cant_go_yet_waits_without_holding_up_this_households()
    {
        const string Old = "aaaa0000bbbb1111aaaa0000bbbb1111";
        using var study = DeviceKeys.Create();
        var leave = new PendingOp(PendingOp.Remove, Old, Device: _desktop.Id);
        _desktop.Store.AddPending(leave);
        _desktop.Store.AddPending(new PendingOp(PendingOp.Add, Household, Sign: Wire.Encode(study.SignPublic), Dh: Wire.Encode(study.DhPublic),
            Proof: Wire.Encode(Wire.SignJoin(study, Household))));
        _relay.Intercept = (request, _) => request.RequestUri!.AbsolutePath.Contains(Old, StringComparison.Ordinal)
            ? FakeRelay.Error(503, "The server is busy; try again later.")
            : null;

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.Pending.ShouldBe([leave]);
        _relay.Members(Household).Keys.ShouldContain(study.DeviceId);
    }

    [Fact]
    public async Task A_removal_or_leave_the_server_answers_with_410_counts_as_done()
    {
        using var other = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        const string Old = "aaaa0000bbbb1111aaaa0000bbbb1111";
        _relay.Seed(Old, _desktop.Keys, other.Keys);
        _relay.Remove(Old, _desktop.Id);                                           // removed there before its own leave went
        _desktop.Store.AddPending(new PendingOp(PendingOp.Remove, Old, Device: other.Id));
        _desktop.Store.AddPending(new PendingOp(PendingOp.Remove, Old, Device: _desktop.Id));

        var run = await _desktop.RunAsync();

        (run.Problem, run.Removed).ShouldBe(((string?)null, false));
        _desktop.Store.Pending.ShouldBeEmpty();
        _desktop.Store.HouseholdId.ShouldBe(Household);
    }

    [Fact]
    public async Task A_member_added_without_the_joiners_own_proof_is_refused_and_dropped()
    {
        using var stranger = DeviceKeys.Create();
        _desktop.Store.AddPending(new PendingOp(PendingOp.Add, Household, Sign: Wire.Encode(stranger.SignPublic), Dh: Wire.Encode(stranger.DhPublic),
            Proof: Wire.Encode(Wire.SignJoin(_desktop.Keys, Household))));

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.Pending.ShouldBeEmpty();
        _relay.Members(Household).Keys.ShouldNotContain(stranger.DeviceId);
    }

    [Fact]
    public async Task Two_rotations_to_the_same_epoch_settle_on_the_one_the_server_took_when_its_sealer_may_hand_it_over()
    {
        var theirs = HouseholdCrypto.NewKey();
        (await _laptop.Client.PostKeysAsync(_laptop.Keys, Household, 2, KeyWrap.For(_laptop.Keys, Household, 2, theirs, [_desktop.AsMember(), _laptop.AsMember()]),
            CancellationToken.None)).Ok.ShouldBeTrue();
        _desktop.Sync.StartRotation(Household);                                   // the desktop's own new key, for epoch 2 too

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.KeyFor(2).ShouldBe(theirs);
        _desktop.Store.Epoch.ShouldBe(2);
        _relay.Epoch(Household).ShouldBe(2);
        _desktop.Store.Pending.ShouldBeEmpty();
        _desktop.Store.RotationKey.ShouldBeNull();
    }

    [Fact]
    public async Task A_rotation_that_loses_its_epoch_to_a_key_a_pc_removed_here_may_hold_rotates_on_to_the_next()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _relay.Seed(Household, study.Keys);
        _desktop.Household.SaveMember(study.AsMember());
        var theirs = HouseholdCrypto.NewKey();                                    // the laptop's new key, sealed to the study PC too
        (await _laptop.Client.PostKeysAsync(_laptop.Keys, Household, 2, KeyWrap.For(_laptop.Keys, Household, 2, theirs,
            [_desktop.AsMember(), _laptop.AsMember(), study.AsMember()]), CancellationToken.None)).Ok.ShouldBeTrue();
        _desktop.Members.Remove(study.Id, Now.ToUnixTimeMilliseconds());           // then the desktop removed it, still at epoch 1
        _desktop.Store.AddPending(new PendingOp(PendingOp.Remove, Household, Device: study.Id));
        _desktop.Sync.StartRotation(Household, forRemoval: true);

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.KeyFor(2).ShouldBe(theirs);                                 // read what came under it
        _desktop.Store.Epoch.ShouldBe(3);                                          // but post under one the study PC never had
        _relay.Sealed(Household, 3).ShouldBe([_desktop.Id, _laptop.Id], ignoreOrder: true);
        _desktop.Store.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rotation_for_a_removal_that_loses_its_epoch_to_the_removed_pcs_own_key_reads_under_it_and_rotates_on()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _relay.Seed(Household, study.Keys);
        _desktop.Household.SaveMember(study.AsMember());
        var theirs = HouseholdCrypto.NewKey();                                    // made while it was a member: its to hand over
        (await study.Client.PostKeysAsync(study.Keys, Household, 2, KeyWrap.For(study.Keys, Household, 2, theirs,
            [_desktop.AsMember(), _laptop.AsMember(), study.AsMember()]), CancellationToken.None)).Ok.ShouldBeTrue();
        _desktop.Members.Remove(study.Id, Now.ToUnixTimeMilliseconds());
        _desktop.Store.AddPending(new PendingOp(PendingOp.Remove, Household, Device: study.Id));
        _desktop.Sync.StartRotation(Household, forRemoval: true);

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.KeyFor(2).ShouldBe(theirs);
        _desktop.Store.Epoch.ShouldBe(3);
        _relay.Sealed(Household, 3).ShouldBe([_desktop.Id, _laptop.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_new_key_waits_while_the_server_lists_a_member_this_pc_doesnt_know_until_a_members_list_brings_it_in()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _relay.Seed(Household, study.Keys);                                       // the laptop added it; the desktop hasn't heard
        _laptop.Household.SaveMember(study.AsMember());
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        _desktop.Sync.StartRotation(Household);

        var waiting = await _desktop.RunAsync();

        (waiting.Problem, waiting.RowsOut).ShouldBe(((string?)null, 0));           // nothing under the old key, and nothing to worry about
        _relay.Epoch(Household).ShouldBe(1);
        _desktop.Store.Pending.ShouldHaveSingleItem().Kind.ShouldBe(PendingOp.Keys);

        _laptop.Household.Upsert([Row(_laptop.Id, 0, 20, changed: 100)]);
        await _laptop.RunAsync();                                                 // its sealed list tells the desktop of the study PC
        await _desktop.RunAsync();
        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _relay.Epoch(Household).ShouldBe(2);
        _relay.Sealed(Household, 2).ShouldBe([_desktop.Id, _laptop.Id, study.Id], ignoreOrder: true);
        _relay.Batches.Where(batch => batch.Device == _desktop.Id).ShouldAllBe(batch => batch.Epoch == 2);
    }

    [Fact]
    public async Task A_new_key_the_server_refuses_stays_queued_and_nothing_goes_up_under_the_old_key_meanwhile()
    {
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 10, changed: 100)]);
        _laptop.Household.Upsert([Row(_laptop.Id, 0, 20, changed: 100)]);
        await _laptop.RunAsync();
        _desktop.Sync.StartRotation(Household);
        _relay.Intercept = (request, _) => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal)
            ? FakeRelay.Error(403, "Not now")
            : null;

        var refused = await _desktop.RunAsync();

        refused.Problem.ShouldNotBeNull().ShouldContain("Not now");
        _desktop.Store.Pending.ShouldHaveSingleItem().Kind.ShouldBe(PendingOp.Keys);
        _desktop.Store.RotationKey.ShouldNotBeNull().Epoch.ShouldBe(2);
        _relay.Batches.ShouldAllBe(batch => batch.Device != _desktop.Id);          // nothing under the old key
        refused.RowsIn.ShouldBe(1);                                                 // what the others sent still comes in

        _relay.Intercept = null;
        var run = await _desktop.RunAsync();

        run.Problem.ShouldBeNull();
        _desktop.Store.Epoch.ShouldBe(2);
        _relay.Batches.Where(batch => batch.Device == _desktop.Id).ShouldNotBeEmpty();
        _relay.Batches.Where(batch => batch.Device == _desktop.Id).ShouldAllBe(batch => batch.Epoch == 2);
    }

    [Fact]
    public async Task A_retried_rotation_posts_the_very_bytes_of_its_first_try_so_a_lost_answer_counts_as_taken()
    {
        _desktop.Sync.StartRotation(Household);
        _relay.LoseAnswer = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal);
        (await _desktop.RunAsync()).Problem.ShouldNotBeNull();                     // the server took it; the answer went astray
        _relay.Epoch(Household).ShouldBe(2);
        _desktop.Store.Epoch.ShouldBe(1);
        _relay.LoseAnswer = null;

        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _desktop.Store.Epoch.ShouldBe(2);                                          // taken as it was, with no rotation after it
        _relay.Epoch(Household).ShouldBe(2);
        var posts = _relay.Sent.Where(sent => sent.Call.EndsWith("/keys", StringComparison.Ordinal)).Select(sent => sent.Body).ToList();
        posts.Count.ShouldBe(2);
        posts[1].ShouldBe(posts[0]);
        (await _laptop.RunAsync()).Problem.ShouldBeNull();
        await _laptop.Sync.CatchUpAsync(_laptop.Keys, CancellationToken.None);
        _laptop.Store.CurrentKey.ShouldBe(_desktop.Store.CurrentKey);
    }

    [Fact]
    public async Task A_new_key_is_sealed_only_to_the_members_the_server_lists_as_current_and_taken_on_only_once_the_server_has_it()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _relay.Seed(Household, study.Keys);
        _desktop.Household.SaveMember(study.AsMember());
        _laptop.Household.SaveMember(study.AsMember());
        _relay.Remove(Household, study.Id);                                       // another member removed it; the desktop hasn't heard
        _desktop.Sync.StartRotation(Household);
        _relay.Intercept = (request, _) => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal)
            ? FakeRelay.Error(503, "The server is busy; try again later.")
            : null;

        (await _desktop.RunAsync()).Problem.ShouldNotBeNull();
        _desktop.Store.Epoch.ShouldBe(1);                                         // kept aside, not used
        _relay.Intercept = null;
        (await _desktop.RunAsync()).Problem.ShouldBeNull();

        _relay.Epoch(Household).ShouldBe(2);
        _relay.Sealed(Household, 2).ShouldBe([_desktop.Id, _laptop.Id], ignoreOrder: true);
        _desktop.Store.Epoch.ShouldBe(2);
        _desktop.Household.Member(study.Id).ShouldNotBeNull().LeftMs.ShouldNotBeNull();
        (await _laptop.RunAsync()).Problem.ShouldBeNull();
        _laptop.Store.Epoch.ShouldBe(2);                                          // the members read found the new epoch's key
        _laptop.Store.CurrentKey.ShouldBe(_desktop.Store.CurrentKey);
    }

    [Fact]
    public async Task A_member_still_posting_under_an_older_key_an_hour_on_gets_a_new_key_sealed_to_it_too()
    {
        var k2 = HouseholdCrypto.NewKey();
        (await _desktop.Client.PostKeysAsync(_desktop.Keys, Household, 2, KeyWrap.For(_desktop.Keys, Household, 2, k2, [_desktop.AsMember(), _laptop.AsMember()]),
            CancellationToken.None)).Ok.ShouldBeTrue();
        _desktop.Store.AddKey(2, k2);
        _relay.Intercept = (request, _) => request.Headers.GetValues("X-PL-Device").Single() == _laptop.Id
            && request.RequestUri!.AbsolutePath.EndsWith("/keys/2", StringComparison.Ordinal)
            ? FakeRelay.Error(404, "There's no key for this PC at that epoch.")    // the laptop's envelope went astray
            : null;
        _laptop.Household.Upsert([Row(_laptop.Id, 0, 1, changed: 100)]);
        await _laptop.RunAsync();
        await _desktop.RunAsync();
        _desktop.Store.Lagging.ShouldContainKey(_laptop.Id);

        _clock.Advance(RelaySync.LagWait);
        _laptop.Household.Upsert([Row(_laptop.Id, 1, 1, changed: 200)]);
        await _laptop.RunAsync();                                                   // still under epoch 1
        await _desktop.RunAsync();                                                  // an hour on: a new key for it
        await _desktop.RunAsync();

        _relay.Epoch(Household).ShouldBe(3);
        _relay.Sealed(Household, 3).ShouldBe([_desktop.Id, _laptop.Id], ignoreOrder: true);
        _desktop.Household.Upsert([Row(_desktop.Id, 0, 3, changed: 300)]);
        await _desktop.RunAsync();
        await _laptop.RunAsync();
        _laptop.Store.Epoch.ShouldBe(3);
        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_member_that_sees_a_removal_makes_a_new_key_without_the_pc_that_went()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _relay.Seed(Household, study.Keys);
        _laptop.Household.SaveMember(study.AsMember());
        _laptop.Household.Upsert([Row(_laptop.Id, 0, 10, changed: 100)]);
        await _laptop.RunAsync();
        _relay.Remove(Household, study.Id);                                       // the study PC left, and made no key
        _clock.Advance(RelaySync.MembersEvery);

        (await _laptop.RunAsync()).Notices.ShouldBe(["Study PC is no longer in the household."]);
        (await _laptop.RunAsync()).Problem.ShouldBeNull();

        _laptop.Store.Epoch.ShouldBe(2);
        _relay.Sealed(Household, 2).ShouldBe([_desktop.Id, _laptop.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_removed_pc_with_its_old_keys_can_neither_pass_off_rows_as_a_members_nor_hand_this_pc_a_key_of_its_choosing()
    {
        using var removed = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _laptop.Household.SaveMember(removed.AsMember());
        _laptop.Members.Remove(removed.Id, Now.ToUnixTimeMilliseconds());           // heard of at epoch 1
        var k2 = HouseholdCrypto.NewKey();
        _laptop.Store.AddKey(2, k2);                                                // the new key it was left out of
        var k3 = HouseholdCrypto.NewKey();

        // (a) rows for the desktop under the old key, signed by the removed PC; (b) its own batch under an epoch of its choosing.
        var forged = Sealed(_key, Household, _desktop.Id, 1, 7, new BatchPlain(1, new WireMember(_desktop.Id, "Desktop-7", "desktop"),
            [Wire.Row(Row(_desktop.Id, 0, 99_999, changed: 500))]), removed.Keys);
        var chosen = Sealed(k3, Household, removed.Id, 3, 8, new BatchPlain(1, new WireMember(removed.Id, "Study PC", "desktop"),
            [Wire.Row(Row(removed.Id, 0, 5, changed: 500))]), removed.Keys);
        var envelope = Wire.Encode(HouseholdCrypto.WrapFor(removed.Keys.Dh, _laptop.Keys.DhPublic, k3, KeyWrap.Context(Household, 3)));
        _relay.Intercept = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/batches", StringComparison.Ordinal))
            {
                return FakeRelay.Json(new System.Text.Json.Nodes.JsonObject
                {
                    ["items"] = new System.Text.Json.Nodes.JsonArray(forged, chosen),
                    ["next"] = 2,
                    ["more"] = false,
                });
            }
            return path.EndsWith("/keys/3", StringComparison.Ordinal)
                ? FakeRelay.Json(new System.Text.Json.Nodes.JsonObject { ["epoch"] = 3, ["from"] = removed.Id, ["body"] = envelope })
                : null;
        };

        await _laptop.RunAsync();

        _laptop.Household.Row(_desktop.Id, Hour(0)).ShouldBeNull();                // (a) not taken
        _laptop.Store.Epoch.ShouldBe(2);                                            // (b) not moved to its key
        _laptop.Store.KeyFor(3).ShouldBeNull();
    }

    [Fact]
    public async Task A_key_is_taken_from_a_pc_that_went_for_an_epoch_before_its_removal_and_never_for_one_after()
    {
        _desktop.Sync.StartRotation(Household);
        await _desktop.RunAsync();                                                  // epoch 2, sealed to the laptop too
        _relay.Remove(Household, _desktop.Id);                                     // then it left, at epoch 2
        var chosen = Wire.Encode(HouseholdCrypto.WrapFor(_desktop.Keys.Dh, _laptop.Keys.DhPublic, HouseholdCrypto.NewKey(), KeyWrap.Context(Household, 3)));
        _relay.Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/keys/3", StringComparison.Ordinal)
            ? FakeRelay.Json(new System.Text.Json.Nodes.JsonObject { ["epoch"] = 3, ["from"] = _desktop.Id, ["body"] = chosen })
            : null;

        (await _laptop.RunAsync()).Notices.ShouldBe(["Desktop-7 is no longer in the household."]);

        _laptop.Store.KeyFor(2).ShouldBe(_desktop.Store.KeyFor(2));               // the key it made while it was in
        _laptop.Store.KeyFor(3).ShouldBeNull();                                    // but none it seals once it has gone
        _laptop.Store.Epoch.ShouldBe(2);
    }

    [Fact]
    public async Task A_removed_pcs_batch_brings_in_nobody_and_its_rows_count_only_up_to_its_removal()
    {
        using var study = new RelayPc("Study PC", ChassisKind.Desktop, _relay, _clock);
        _relay.Seed(Household, study.Keys);
        _laptop.Household.SaveMember(study.AsMember());
        _relay.Remove(Household, study.Id);                                       // removed at epoch 1
        var k2 = HouseholdCrypto.NewKey();
        _laptop.Store.AddKey(2, k2);                                                // the new key it was left out of
        using var ghost = DeviceKeys.Create();
        var farAhead = Now.AddYears(5).ToUnixTimeMilliseconds();
        List<WireMember> list =
        [
            new(ghost.DeviceId, "Ghost", "desktop", Wire.Encode(ghost.SignPublic), Wire.Encode(ghost.DhPublic), Added: farAhead),
            new(study.Id, "Study PC", "desktop", Wire.Encode(study.Keys.SignPublic), Wire.Encode(study.Keys.DhPublic), Added: farAhead),
        ];
        var device = new WireMember(study.Id, "Study PC", "desktop");
        var before = Sealed(_key, Household, study.Id, 1, 1, new BatchPlain(1, device, [Wire.Row(Row(study.Id, 0, 5, changed: 500))], list), study.Keys);
        var after = Sealed(k2, Household, study.Id, 2, 2, new BatchPlain(1, device, [Wire.Row(Row(study.Id, 1, 6, changed: 600))], list), study.Keys);
        _relay.Intercept = (request, _) => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/batches", StringComparison.Ordinal)
            ? FakeRelay.Json(new System.Text.Json.Nodes.JsonObject
            {
                ["items"] = new System.Text.Json.Nodes.JsonArray(before, after),
                ["next"] = 2,
                ["more"] = false,
            })
            : null;

        await _laptop.RunAsync();

        _laptop.Household.Member(ghost.DeviceId).ShouldBeNull();                    // nobody comes in on its word
        _laptop.Household.Member(study.Id).ShouldNotBeNull().LeftMs.ShouldNotBeNull();   // nor does it bring itself back
        _laptop.Household.Row(study.Id, Hour(0)).ShouldNotBeNull().EnergyWh.ShouldBe(5);   // its own rows from before its removal
        _laptop.Household.Row(study.Id, Hour(1)).ShouldBeNull();                    // none under a key after it
    }

    public void Dispose()
    {
        _desktop.Dispose();
        _laptop.Dispose();
    }

    private static long Hour(int hour) => Now.AddDays(-1).AddHours(hour).ToUnixTimeMilliseconds();

    /// <summary>As if the PC had just posted all its rows under its current key, so a test sees only what it sets going.</summary>
    private void AsIfSnapshotJustWent(RelayPc pc)
    {
        pc.Store.SnapshotEpoch = pc.Store.Epoch;
        pc.Store.SnapshotAt = _clock.GetUtcNow().ToUnixTimeMilliseconds();
    }

    private static HouseholdRow Row(string device, int hour, double energyWh, long changed) => new(
        device, Hour(hour), energyWh, 1, 1, 1, 1, 0, 0, 3600, 0, 0, 3600, 0, 0, 1_000, "GBP", changed);

    /// <summary>A batch item as the server hands it on, sealed under <paramref name="key"/> and signed by <paramref name="signer"/>.</summary>
    private static System.Text.Json.Nodes.JsonObject Sealed(byte[] key, string household, string device, int epoch, long seq, BatchPlain plain, DeviceKeys signer)
    {
        var aad = HouseholdCrypto.BatchAad(household, device, epoch, seq);
        var body = HouseholdCrypto.Seal(key, PowerLedger.Service.Sharing.SharingClient.Gzip(HouseholdJson.Bytes(plain, HouseholdJson.Default.BatchPlain)), aad);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["seq"] = seq, ["device"] = device, ["epoch"] = epoch, ["body"] = Wire.Encode(body),
            ["sig"] = Wire.Encode(HouseholdCrypto.SignData(signer.Sign, HouseholdCrypto.BatchToSign(aad, body))),
        };
    }

    private static PowerLedger.Core.Aggregate Aggregate(DateTimeOffset start) => new(
        start, AvgW: 50, MaxW: 90, EnergyWh: 50, CpuWh: 12, GpuWh: 8, DisplayWh: 5, RestWh: 25, IdleOnWh: 3, IdleOffWh: 1,
        IdleOnSeconds: 400, IdleOffSeconds: 300, OnSeconds: 3500, BatterySeconds: 900, GapSeconds: 100, SampleCount: 3500,
        MeasuredSeconds: 2000, CalibratedSeconds: 1000, EstimatedSeconds: 500);

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
            Members = new MemberBook(Store, Household);
        }

        public MemberBook Members { get; }

        public string Name { get; }

        public ChassisKind Kind { get; }

        public HouseholdStore Store { get; }

        public DeviceKeys Keys { get; }

        public string Id => Keys.DeviceId;

        public HouseholdRepository Household { get; }

        public RelayClient Client { get; }

        public RelaySync Sync { get; }

        /// <summary>This PC's hour totals, which its rows are built from.</summary>
        public AggregateRepository Aggregates => new(_database.Db);

        public HourRows Rows => new(Aggregates, new TariffRepository(_database.Db), Household);

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
