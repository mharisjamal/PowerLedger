using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Storage.Tests;

/// <summary>Storage version 3 (households design §1): every member PC's hour rows and the members as this PC knows them.</summary>
public sealed class HouseholdRepositoryTests : IDisposable
{
    private const string Laptop = "0123456789abcdef0123456789abcdef";
    private const string Desktop = "fedcba9876543210fedcba9876543210";

    private readonly TestDatabase _database = new();

    private HouseholdRepository Repository => new(_database.Db);

    [Fact]
    public void A_version_2_database_moves_to_3_keeping_its_data_and_gaining_the_household_tables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"powerledger-v2-{Guid.NewGuid():N}.db");
        try
        {
            MakeVersion2(path);

            using var db = SqliteDatabase.OpenAndMigrate(path);
            using var c = db.Open();
            Migrator.CurrentVersion(c).ShouldBe(3);
            Scalar(c, "SELECT COUNT(*) FROM samples_1h").ShouldBe(1);
            Scalar(c, "SELECT sample_count FROM samples_1h").ShouldBe(3600);
            Scalar(c, "SELECT COUNT(*) FROM outbox_events").ShouldBe(1);
            new SettingsRepository(db).Get("sharing.minute").ShouldBe("42");
            Columns(c, "household_rows").ShouldBe(
            [
                "device_id", "hour_ms", "energy_wh", "cpu_wh", "gpu_wh", "display_wh", "rest_wh", "idle_on_wh", "idle_off_wh",
                "on_s", "battery_s", "idle_s", "measured_s", "calibrated_s", "estimated_s", "cost_micro", "currency", "changed_ms",
            ]);
            Columns(c, "household_members").ShouldBe(
                ["device_id", "name", "kind", "sign_key", "dh_key", "added_ms", "left_ms", "last_synced_ms"]);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm", path + ".bak" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void The_upsert_takes_a_row_that_changed_later_and_ignores_one_that_changed_earlier_or_at_the_same_time()
    {
        Repository.Upsert([Row(Laptop, 0, energyWh: 10, changedMs: 5_000)]).ShouldBe(1);

        Repository.Upsert([Row(Laptop, 0, energyWh: 11, changedMs: 4_000)]).ShouldBe(0);
        Repository.Upsert([Row(Laptop, 0, energyWh: 12, changedMs: 5_000)]).ShouldBe(0);
        Repository.Row(Laptop, 0).ShouldBe(Row(Laptop, 0, energyWh: 10, changedMs: 5_000));

        Repository.Upsert([Row(Laptop, 0, energyWh: 13, changedMs: 6_000), Row(Desktop, 0, energyWh: 20, changedMs: 1_000)]).ShouldBe(2);
        Repository.Row(Laptop, 0).ShouldBe(Row(Laptop, 0, energyWh: 13, changedMs: 6_000));
        Repository.Row(Desktop, 0).ShouldBe(Row(Desktop, 0, energyWh: 20, changedMs: 1_000));
    }

    [Fact]
    public void A_row_without_a_tariff_keeps_no_cost_and_no_currency()
    {
        var row = Row(Laptop, 0, energyWh: 10, changedMs: 1) with { CostMicro = null, Currency = null };

        Repository.Upsert([row]);

        Repository.Row(Laptop, 0).ShouldBe(row);
    }

    [Fact]
    public void Rows_come_back_by_device_and_hour_and_by_when_they_changed()
    {
        Repository.Upsert(
        [
            Row(Laptop, 0, changedMs: 300), Row(Laptop, Hour, changedMs: 100), Row(Laptop, 2 * Hour, changedMs: 200),
            Row(Desktop, 0, changedMs: 900),
        ]);

        Repository.RowsBetween(Laptop, 0, 2 * Hour).Select(row => row.HourMs).ShouldBe([0L, Hour]);
        Repository.ChangedAfter(Laptop, 100).Select(row => row.HourMs).ShouldBe([2 * Hour, 0L]);
        Repository.Latest().ShouldBe(new Dictionary<string, long> { [Laptop] = 300, [Desktop] = 900 }, ignoreOrder: true);

        Repository.DeleteRows(Laptop).ShouldBe(3);
        Repository.Latest().Keys.ShouldBe([Desktop]);
    }

    [Fact]
    public void A_member_is_kept_renamed_marked_left_and_synced_and_its_first_time_stays()
    {
        byte[] sign = [1, 2, 3], dh = [4, 5, 6];
        Repository.SaveMember(new HouseholdMember(Laptop, "Laptop-2", ChassisKind.Laptop, sign, dh, AddedMs: 1_000, LeftMs: null, LastSyncedMs: null));
        Repository.Synced(Laptop, 5_000);
        Repository.Synced(Laptop, 4_000);

        Repository.SaveMember(new HouseholdMember(Laptop, "Kitchen laptop", ChassisKind.Laptop, sign, dh, AddedMs: 9_000, LeftMs: null, LastSyncedMs: null));

        var member = Repository.Member(Laptop).ShouldNotBeNull();
        member.Name.ShouldBe("Kitchen laptop");
        member.Kind.ShouldBe(ChassisKind.Laptop);
        member.SignKey.ShouldBe(sign);
        member.DhKey.ShouldBe(dh);
        member.AddedMs.ShouldBe(1_000);
        member.LastSyncedMs.ShouldBe(5_000);
        member.LeftMs.ShouldBeNull();

        Repository.MarkLeft(Laptop, 7_000);
        Repository.Member(Laptop).ShouldNotBeNull().LeftMs.ShouldBe(7_000);
        Repository.Members().ShouldHaveSingleItem().DeviceId.ShouldBe(Laptop);

        Repository.DeleteMember(Laptop);
        Repository.Members().ShouldBeEmpty();
    }

    public void Dispose() => _database.Dispose();

    private const long Hour = 3_600_000;

    private static HouseholdRow Row(string device, long hourMs, double energyWh = 10, long changedMs = 1) => new(
        device, hourMs, energyWh, CpuWh: 4, GpuWh: 2, DisplayWh: 1.5, RestWh: 2.5, IdleOnWh: 0.5, IdleOffWh: 0.25,
        OnS: 3600, BatteryS: 600, IdleS: 120, MeasuredS: 3000, CalibratedS: 500, EstimatedS: 100, CostMicro: 2_500, Currency: "GBP",
        ChangedMs: changedMs);

    /// <summary>A database as version 2 left it: an hour row, an outbox event and a setting.</summary>
    private static void MakeVersion2(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Migrator.Exec(c, "CREATE TABLE schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
        Migrator.Exec(c, Schema.V1);
        Migrator.Exec(c, Schema.V2);
        Migrator.Exec(c, "INSERT INTO schema_version(version, applied_ms) VALUES (1, 0), (2, 0)");
        Migrator.Exec(c, "INSERT INTO samples_1h (start_ms, avg_w, max_w, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, idle_on_wh, " +
            "idle_off_wh, idle_on_s, idle_off_s, on_s, battery_s, gap_s, sample_count, measured_s, calibrated_s, estimated_s) " +
            "VALUES (0, 30, 40, 30, 10, 5, 5, 10, 1, 0, 60, 0, 3600, 0, 0, 3600, 3600, 0, 0)");
        Migrator.Exec(c, "INSERT INTO outbox_events (day, kind, json) VALUES ('2026-09-23', 'usage', '{}')");
        Migrator.Exec(c, "INSERT INTO settings (key, value) VALUES ('sharing.minute', '42')");
    }

    private static List<string> Columns(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
