namespace PowerLedger.Storage;

/// <summary>DDL per schema version. Never edit a shipped version; add a new one.</summary>
internal static class Schema
{
    public const string V1 = """
        CREATE TABLE samples_raw (
            ts_ms        INTEGER PRIMARY KEY,
            delta_s      REAL    NOT NULL,
            total_w      REAL    NOT NULL,
            quality      INTEGER NOT NULL,
            cpu_w        REAL    NOT NULL,
            gpu_w        REAL    NOT NULL,
            display_w    REAL    NOT NULL,
            ram_w        REAL    NOT NULL,
            storage_w    REAL    NOT NULL,
            board_w      REAL    NOT NULL,
            extras_w     REAL    NOT NULL,
            monitors_w   REAL    NOT NULL,
            psu_loss_w   REAL    NOT NULL,
            rest_w       REAL    NOT NULL,
            on_battery   INTEGER NOT NULL,
            display_on   INTEGER NOT NULL,
            user_idle    INTEGER NOT NULL,
            locked       INTEGER NOT NULL,
            cpu_load     REAL    NOT NULL,
            gpu_load     REAL,
            brightness   REAL,
            suspect      INTEGER NOT NULL
        );

        CREATE TABLE samples_1m (
            start_ms     INTEGER PRIMARY KEY,
            avg_w        REAL NOT NULL, max_w REAL NOT NULL,
            energy_wh    REAL NOT NULL, cpu_wh REAL NOT NULL, gpu_wh REAL NOT NULL, display_wh REAL NOT NULL, rest_wh REAL NOT NULL,
            idle_on_wh   REAL NOT NULL, idle_off_wh REAL NOT NULL,
            idle_on_s    REAL NOT NULL, idle_off_s REAL NOT NULL,
            on_s         REAL NOT NULL, battery_s REAL NOT NULL, gap_s REAL NOT NULL,
            sample_count INTEGER NOT NULL,
            measured_s   REAL NOT NULL, calibrated_s REAL NOT NULL, estimated_s REAL NOT NULL
        );

        CREATE TABLE samples_1h (
            start_ms     INTEGER PRIMARY KEY,
            avg_w        REAL NOT NULL, max_w REAL NOT NULL,
            energy_wh    REAL NOT NULL, cpu_wh REAL NOT NULL, gpu_wh REAL NOT NULL, display_wh REAL NOT NULL, rest_wh REAL NOT NULL,
            idle_on_wh   REAL NOT NULL, idle_off_wh REAL NOT NULL,
            idle_on_s    REAL NOT NULL, idle_off_s REAL NOT NULL,
            on_s         REAL NOT NULL, battery_s REAL NOT NULL, gap_s REAL NOT NULL,
            sample_count INTEGER NOT NULL,
            measured_s   REAL NOT NULL, calibrated_s REAL NOT NULL, estimated_s REAL NOT NULL
        );

        CREATE TABLE sessions (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER,
            reason     TEXT    NOT NULL,
            end_reason TEXT
        );
        CREATE INDEX ix_sessions_start ON sessions(start_ms);

        CREATE TABLE tariffs (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            effective_from_ms INTEGER NOT NULL,
            price_micro       INTEGER NOT NULL,
            currency          TEXT    NOT NULL
        );

        CREATE TABLE calibration (
            inventory_hash TEXT    NOT NULL,
            bucket         INTEGER NOT NULL,
            baseline_w     REAL    NOT NULL,
            samples        INTEGER NOT NULL,
            updated_ms     INTEGER NOT NULL,
            PRIMARY KEY (inventory_hash, bucket)
        );

        CREATE TABLE hardware_inventory (
            hash        TEXT PRIMARY KEY,
            detected_ms INTEGER NOT NULL,
            json        TEXT    NOT NULL
        );

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
