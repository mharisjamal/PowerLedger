# PowerLedger Plan M: opt-in data sharing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** users choose, on a standard consent dialog, which of four kinds of data PowerLedger sends. The service
collects and uploads what they allowed once a day, and a Cloudflare Worker stores it for the owner to export, share or
sell. Built to `docs/superpowers/specs/2026-09-24-powerledger-data-sharing-design.md` ("the design"), released as 0.6.0.

**Architecture:**
- **Consent** is machine-wide and lives in the service, outside `ServiceSettings`, so a settings save can never overwrite
  it.
- **The service** builds minute rows from `samples_raw` into an outbox every few minutes, then sends one gzip JSON report
  per complete local day to `POST /v1/report`.
- **The App** shows the dialog and the Privacy section, and passes crashes and usage counts to the service over the pipe.
- **The Worker** validates each report against the shared schema, stores the body as sent in R2, indexes it in D1, and
  serves the owner's export.

**Tech stack:**
- .NET 10: WPF App and Worker service, SQLite, xUnit, Shouldly, JsonSchema.Net.
- The Worker: TypeScript on Cloudflare Workers, R2 and D1, `@cfworker/json-schema`, vitest with
  `@cloudflare/vitest-pool-workers`, wrangler.

---

## Ground rules for every agent

- **Where to work.** Only in your own worktree and branch, named in your section. Set `TEMP` and `TMP` to a private
  folder: `mkdir -p "$TEMP/pl-m-<you>"` and prefix every `dotnet` and `npm` command with
  `TEMP="$TEMP/pl-m-<you>" TMP="$TEMP/pl-m-<you>"`.
- **Test-first.** For each step: the failing test, run it and watch it fail, the code, run it and watch it pass, then
  commit.
- **.NET tests.** Always filter `--filter "Category!=Hardware&Category!=UI&Category!=Installed"`, because Installed tests
  talk to the real service on this PC. Never start, stop or reinstall the PowerLedger service.
- **Line endings.** The working tree is CRLF. Edit existing files with the Edit tool. Git Bash `sed -i` and Python text
  writes turn a file LF. Write new `.cs` and `.xaml` files with CRLF (convert as bytes after writing); JSON and TS files
  may be LF.
- **Commits.**
  - Commit after each task, staging paths explicitly (never `git add -A`).
  - Messages are one plain English sentence saying what changed, like the history
    (`Add …`, `Send …`, `Keep …`), with no attribution or co-author lines.
  - Never push.
- **Stay in your lane.** Don't touch another agent's area. Task 0 is done: build on it, don't edit it. If it's wrong,
  stop and report.
- **Wording.** User-facing text is plain English, sentence case, no exclamation marks. Wording given below is used
  exactly.

## Task 0 — the contract (lead, done)

Commits `24c710f` and `93a8bee` on `plan-m/base`:

| File | What it is |
|---|---|
| `src/PowerLedger.Contracts/Sharing.cs` | `ConsentText.Version` (1); `Consent(Version, Diagnostics, Usage, Power, Share)` with `Unanswered`, `Answered`, `AllowsAny` and `Validate()`; `SharingStatus`; `UsageCounts` with `Validate()` (the counts add up and the states replace); `CrashReport` with `Trimmed()` and `Validate()` |
| `src/PowerLedger.Contracts/PipeMessages.cs` | `SetConsentRequest`, `ReportUsageRequest`, `ReportCrashRequest`, `PreviewUploadRequest`, `SendNowRequest`, `DeleteMyDataRequest` and `SharingReply(Id, Ok, Message, Path)`, registered as `setConsent`, `reportUsage`, `reportCrash`, `previewUpload`, `sendNow`, `deleteMyData` and `sharing` |
| `src/PowerLedger.Contracts/ServiceStatus.cs` | a trailing `SharingStatus? Sharing = null` |
| `src/PowerLedger.Core/Scrubber.cs` | `Scrubber.Scrub(text, ScrubNames)` and `ScrubNames.Here()` |
| `server/schema/report-v1.schema.json` | the report, version 1. The minutes' columns are only checked as arrays of at most 1500 items; the Worker checks their contents in code |
| `server/test/fixtures/` | `valid-*.json` pass everything. `invalid-schema-*.json` fail the schema. `invalid-minutes-*.json` pass the schema and must fail the Worker's minutes check |
| `tests/PowerLedger.Service.Tests/SharingContractTests.cs` | the contract tests; the csproj links the schema and fixtures into `Contract/` in the output |
| `tests/PowerLedger.Core.Tests/ScrubberTests.cs` | the scrubber's tests |

**The wire, in short.** Each upload is one object: the header, then `diagnostics`, `usage` and `power`, each present only
when its switch is on. `valid-full.json` is the reference: every field name, nesting and null is as there.

---

## Wave 1 — three agents in parallel

### Agent S — the Worker

Worktree `D:\PowerLedger-m\server`, branch `plan-m/server` from `plan-m/base`. Everything under `server/`, plus one
job in `.github/workflows/ci.yml`. Downloads from npmjs.com are approved by the owner.

#### S1. Scaffold

- **Files:** `server/package.json`, `server/package-lock.json`, `server/tsconfig.json`, `server/wrangler.toml`,
  `server/vitest.config.ts`, `server/test/env.d.ts`, `server/test/apply-migrations.ts`, `server/.gitignore`
  (`node_modules/`, `.wrangler/`, `.dev.vars`).
- **Packages:**
  - `npm install --save-dev wrangler typescript vitest @cloudflare/vitest-pool-workers @cloudflare/workers-types`,
    choosing the vitest version that `@cloudflare/vitest-pool-workers` declares as its peer;
  - `npm install @cfworker/json-schema`.
- **Scripts:**
  - `"test": "vitest run && node --test tools/"`
  - `"deploy": "wrangler deploy"`
  - `"dev": "wrangler dev"`
  - `"types": "wrangler types"`
- **`wrangler.toml`:** check the rate-limit binding's syntax against the installed wrangler's own docs; if
  `[[ratelimits]]` isn't known, use `[[unsafe.bindings]]` with `type = "ratelimit"`.

```toml
name = "powerledger-data"
main = "src/index.ts"
compatibility_date = "2026-09-01"

[[r2_buckets]]
binding = "REPORTS"
bucket_name = "powerledger-data"

[[d1_databases]]
binding = "DB"
database_name = "powerledger-index"
database_id = "00000000-0000-0000-0000-000000000000"   # the lead sets the real id at deploy (L5)
migrations_dir = "migrations"

[[ratelimits]]
name = "ADDRESS_LIMIT"
namespace_id = "1001"
simple = { limit = 60, period = 60 }

[triggers]
crons = ["17 3 * * *"]
```

- **`vitest.config.ts`**, following the pool's current docs:
  - `readD1Migrations("migrations")` passed in as the `TEST_MIGRATIONS` binding;
  - `ADMIN_TOKEN = "test-admin-token"`;
  - `singleWorker: true`;
  - `wrangler: { configPath: "./wrangler.toml" }`.
- **`test/apply-migrations.ts`** runs `applyD1Migrations(env.DB, env.TEST_MIGRATIONS)`.
- **`src/index.ts`** starts as a `fetch` that returns 404 JSON `{"error":"Not found."}`. A first test checks that.

#### S2. D1 schema — `server/migrations/0001_init.sql`

```sql
CREATE TABLE installs (
  id              TEXT PRIMARY KEY,
  key_hash        TEXT NOT NULL,             -- SHA-256 hex of the install key, set by the first request (trust on first use)
  first_seen      INTEGER NOT NULL,          -- ms
  last_seen       INTEGER NOT NULL,
  consent_version INTEGER NOT NULL,
  diagnostics     INTEGER NOT NULL,
  usage           INTEGER NOT NULL,
  power           INTEGER NOT NULL,
  share           INTEGER NOT NULL,
  country         TEXT,
  app_version     TEXT
);
CREATE TABLE reports (
  install_id  TEXT NOT NULL,
  day         TEXT NOT NULL,                  -- the PC's local day, yyyy-MM-dd
  received_at INTEGER NOT NULL,
  bytes       INTEGER NOT NULL,               -- as sent, compressed
  sections    TEXT NOT NULL,                  -- e.g. "diagnostics,power"
  country     TEXT NOT NULL,
  r2_key      TEXT NOT NULL,
  PRIMARY KEY (install_id, day)
);
CREATE INDEX reports_day ON reports(day);
CREATE TABLE requests (
  install_id TEXT NOT NULL,
  utc_day    TEXT NOT NULL,
  count      INTEGER NOT NULL,
  PRIMARY KEY (install_id, utc_day)
);
CREATE TABLE tombstones (
  id         TEXT PRIMARY KEY,
  deleted_at INTEGER NOT NULL
);
```

#### S3. Bounded gunzip — `server/src/gzip.ts`

`gunzipBounded(bytes: Uint8Array, max: number): Promise<Uint8Array | null>` streams through
`DecompressionStream("gzip")`, counts the bytes as they come, and cancels and returns `null` past `max`. Corrupt gzip
throws; the caller turns that into 400.

**Tests:** round trip of a small body; a zip bomb (10 MB of zeros, compressed) against an 8 MB limit returns `null`; bytes
that aren't gzip throw.

#### S4. Minutes check — `server/src/minutes.ts`

`checkMinutes(m: Record<string, unknown>): string | null` returns null when good, or the first problem as a sentence.
The rules:
- Every column is an array of the same length, at most 1500.
- `t` holds integers 0–1499, strictly rising.
- Numbers must be finite. Integers must be integers.

| Columns | Range |
|---|---|
| `avgW maxW cpuW gpuW displayW ramW storageW boardW extrasW monitorsW psuLossW` | 0–5000 |
| `unattributedW` | −5000–5000 |
| `cpuLoad` | 0–1, never null |
| `gpuLoad`, `brightness` | 0–1 or null |
| `displayOnS idleS lockedS batteryS measuredS calibratedS estimatedS` | 0–120 |
| `samples` | integer 0–100000 |
| `totalSource` | integer 0–4 |
| `gpuScope` | integer 0–2 |
| `measuredMask` | integer 0–7 |

It is one loop per column, with no allocation per item.

**Tests:** each `valid-*.json` passes; each `invalid-minutes-*.json` fails; plus NaN given as a string, a float in
`samples`, an empty day (all lengths 0) passing, and 1501 items failing.

#### S5. `POST /v1/report` — `server/src/report.ts`, `server/src/auth.ts`, `server/src/schema.ts`

The steps, in order. Each refusal is `{"error": "<sentence>"}` with the status given.
1. `Authorization: Bearer <key>` is required, the key 43 base64url characters; otherwise 401.
2. `Content-Encoding: gzip` is required, and the body must be at most 1,048,576 bytes; otherwise 413. Read the whole
   body as bytes.
3. `env.ADDRESS_LIMIT.limit({ key: request.headers.get("CF-Connecting-IP") ?? "unknown" })` must succeed; otherwise 429.
4. `gunzipBounded(body, 8 MiB)`; null or throwing gives 413 or 400. Then `JSON.parse`; failing gives 400.
5. The schema:
   - `new Validator(schema, "2020-12", false)` built once at module scope, from `schema/report-v1.schema.json`, imported
     as JSON;
   - an invalid report gets 400 with the first error's `instanceLocation` and `error`.
6. `checkMinutes(power.minutes)` when `power` is present; a problem gives 400.
7. `day` must lie between the UTC date 15 days back and tomorrow; otherwise 400.
8. The install:
   - a tombstone gives 410;
   - an unknown install records `sha256hex(key)` (trust on first use);
   - a different hash gives 403.
   - Compare with `crypto.subtle.timingSafeEqual` over equal-length encodings.
9. Request count: upsert `requests(install_id, utc_day)` with `count = count + 1 RETURNING count`; over 20 gives 429.
10. Store:
    - `REPORTS.put("reports/v1/<installId>/<day>.json.gz", body, { httpMetadata: { contentType: "application/json", contentEncoding: "gzip" }, customMetadata: { country, receivedAt, app } })`;
    - upsert the `reports` row, a day sent again replacing the first;
    - upsert `installs` with the consent, `last_seen`, `country` and `app_version`.
    - The country is `request.cf?.country` or `"XX"`.
11. Reply 200 `{"ok":true}`.

`auth.ts` holds `sha256hex`, `bearer(request)`, `checkInstall(env, id, key): "new" | "ok" | "mismatch" | "gone"`, and
`countRequest(env, id)`, which `/v1/consent` and `/v1/delete` use too.

**Tests,** using `SELF.fetch` with fixtures gzipped in the test:
- every `valid-*` gives 200, with the R2 object's bytes equal to what was sent and a D1 row;
- every `invalid-*` gives 400;
- a missing bearer gives 401, a body over 1 MB 413, a zip bomb 413;
- the same install with another key gives 403;
- a day 16 days old gives 400;
- a day sent twice leaves one object and one row;
- the 21st request of the day gives 429;
- a tombstoned install gives 410;
- a request with `cf.country = "PK"` stores PK.

#### S6. `POST /v1/consent` and `POST /v1/delete` — `server/src/install.ts`

- **`/v1/consent`:**
  - body `{"installId": "<guid>", "consent": {version, diagnostics, usage, power, share}}`, plain JSON of at most 4 KB,
    checked with the schema's `consent` definition and the GUID pattern;
  - the same auth, trust on first use, count and tombstone rules as a report;
  - upserts `installs`' switches and `last_seen`, then 200.
- **`/v1/delete`:**
  - body `{"installId": "<guid>"}`, same auth;
  - for a known install with a matching key:
    - list its `reports` rows and `REPORTS.delete` their keys, 1000 at a time;
    - also list the R2 prefix `reports/v1/<id>/` and delete anything left;
    - delete its `reports`, `requests` and `installs` rows;
    - insert a tombstone;
    - reply 200.
  - An unknown install is tombstoned too, and gets 200.
  - An already tombstoned one gets 410. A mismatched key gets 403.

**Tests:** consent is recorded; consent with `share` but no `power` gives 400; delete removes the objects and the rows,
then a report gives 410; delete with a wrong key gives 403 and removes nothing.

#### S7. The owner's endpoints — `server/src/admin.ts`

- **Auth.** `Authorization: Bearer <ADMIN_TOKEN>`, compared timing-safe; otherwise 401. An empty or missing
  `ADMIN_TOKEN` refuses everything.
- **`GET /admin/stats`:**
  - `{installs, switches: {diagnostics, usage, power, share}, days: [{day, reports, bytes}]}`;
  - the last 30 days, newest first.
- **`GET /admin/list?from=yyyy-MM-dd&to=yyyy-MM-dd&shared=1&after=<day>|<installId>&limit=1000`:**
  - `{items: [{key, installId, day, country, receivedAt, bytes, sections}], next}`;
  - ordered by `day, install_id`;
  - `shared=1` keeps only installs whose current `share` is 1;
  - tombstoned installs never appear;
  - `next` is null at the end.
- **`GET /admin/object?key=`:**
  - streams the R2 body with `Content-Type: application/gzip` and no `Content-Encoding`, so a client gets the bytes
    as stored;
  - a key outside `reports/v1/` gives 400; a missing one 404.

**Tests:** no token gives 401; stats counts right; `list` pages with `after`; `shared=1` leaves out a non-sharing
install; `object` returns the bytes as stored.

#### S8. Retention — `server/src/retention.ts`

`scheduled()` computes `cutoff`, the UTC date 3 years back. It deletes the R2 objects and `reports` rows with
`day < cutoff`, 1000 at a time and at most 10 batches a run. It also deletes `requests` rows older than 2 days.

**Test:** it runs `scheduled` through the pool's helper, and old rows and objects go while recent ones stay.

#### S9. Owner tools and docs

- **`server/tools/set-admin-token.ps1`:**
  - The owner runs it; nobody else sees the token.
  - It reads `%USERPROFILE%\.powerledger\admin.json` if it exists. Otherwise it makes one:
    `{ "token": <32 random bytes, base64url>, "salt": <32 random bytes, base64url> }`, from
    `[Security.Cryptography.RandomNumberGenerator]::GetBytes(32)`. It then restricts the file to the owner with
    `icacls /inheritance:r /grant:r "$env:USERNAME:(R,W)"`.
  - It pipes the token to `npx wrangler secret put ADMIN_TOKEN` and prints only "Admin token set.".
  - It keeps an existing salt, so pseudonyms stay the same across exports.
- **`server/tools/export.mjs`:** Node 20 or later, no dependencies.
  - Command: `node tools/export.mjs --url <worker> --from <day> --to <day> [--shared] --out <folder>`.
  - It reads the token and salt from that file, pages through `/admin/list`, fetches each `/admin/object`, gunzips it
    with `zlib`, and replaces `installId` with `pc`, the first 16 hex characters of HMAC-SHA256(salt, installId).
  - It adds `country` and `receivedAt`, then writes:
    - `reports.ndjson`: one report a line, minutes kept columnar;
    - `minutes.csv`: one row a minute, `pc,day,country,chassis,arch,t,<every minutes column>`;
    - `hardware.ndjson`: `pc`, `day` and `hardware`, where present.
  - The pure parts (`pseudonym`, `minuteRows`, `csvLine`) are exported and tested in `tools/export.test.mjs` with
    `node:test`.
- **`server/README.md`:** what the Worker does, how to run the tests, the one-time setup (L5's commands), how to
  export, and that ADMIN_TOKEN and the salt live only in the owner's profile.

#### S10. CI

Add to `.github/workflows/ci.yml` a job `server` on `ubuntu-latest`:
- checkout and `actions/setup-node`, with Node 22 and npm cache keyed on `server/package-lock.json`, using the same
  action versions the file already uses;
- then `npm ci` and `npm test` in `server/`.

It needs nothing from the Windows jobs.

---

### Agent C — the collector, in the service

Worktree `D:\PowerLedger-m\service`, branch `plan-m/service` from `plan-m/base`. The areas:
- `src/PowerLedger.Storage`, `src/PowerLedger.Service` and the service's tests;
- the raw writer's inputs in `src/PowerLedger.Core`, if needed;
- `PipeHandler`.

No App files.

#### C1. Storage version 2 — `Schema.cs`, `Migrator.cs`

Add `Schema.V2` and `(2, Schema.V2)`. Never edit V1.

```sql
ALTER TABLE samples_raw ADD COLUMN total_source  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE samples_raw ADD COLUMN gpu_scope     INTEGER NOT NULL DEFAULT 0;
ALTER TABLE samples_raw ADD COLUMN measured_mask INTEGER NOT NULL DEFAULT 0;
CREATE TABLE outbox_minutes (
    start_ms INTEGER PRIMARY KEY, day TEXT NOT NULL, minute INTEGER NOT NULL,
    avg_w REAL NOT NULL, max_w REAL NOT NULL,
    cpu_w REAL NOT NULL, gpu_w REAL NOT NULL, display_w REAL NOT NULL, ram_w REAL NOT NULL, storage_w REAL NOT NULL,
    board_w REAL NOT NULL, extras_w REAL NOT NULL, monitors_w REAL NOT NULL, psu_loss_w REAL NOT NULL,
    unattributed_w REAL NOT NULL,
    cpu_load REAL NOT NULL, gpu_load REAL, brightness REAL,
    display_on_s REAL NOT NULL, idle_s REAL NOT NULL, locked_s REAL NOT NULL, battery_s REAL NOT NULL,
    measured_s REAL NOT NULL, calibrated_s REAL NOT NULL, estimated_s REAL NOT NULL,
    samples INTEGER NOT NULL, total_source INTEGER NOT NULL, gpu_scope INTEGER NOT NULL, measured_mask INTEGER NOT NULL
);
CREATE INDEX outbox_minutes_day ON outbox_minutes(day);
CREATE TABLE outbox_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    day TEXT NOT NULL,
    kind TEXT NOT NULL,         -- 'crash' (one row each), 'usage' and 'sources' (one row a day, merged)
    json TEXT NOT NULL
);
CREATE INDEX outbox_events_day ON outbox_events(day, kind);
```

**Tests:**
- a version 1 database migrates to 2, with old raw rows reading 0 in the new columns;
- a fresh database is at 2;
- the backup is taken before the upgrade, as today.

#### C2. Write what each reading measured

`RawSampleRepository.InsertBatch` writes `total_source` (`(int)TotalSource`), `gpu_scope` and `measured_mask`, taken
from the reading. The mask's bits:
- **1** when the processor's power came from the energy meter (the sample's `CpuPackageW` has a value);
- **2** when the graphics watts came from a vendor library (`DGpuW` has a value; check that only `nvidia-gpu`, `amd-gpu`
  and `arc-gpu` set it);
- **4** when `TotalSource` isn't `Model`.

Thread whatever the raw row type needs from `SamplingLoop` without changing what the model computes.

**Test:** rows written and read back carry all three.

#### C3. Minute rows — `src/PowerLedger.Service/Sharing/MinuteBuilder.cs`

This is pure. `Build(IReadOnlyList<RawRow> rows, TimeZoneInfo zone) -> IReadOnlyList<MinuteRow>`. `RawRow` is the
row type `RawSampleRepository` reads back. If it has none that carries every column, add a `ReadRange(fromMs, toMs)`
returning a small record with them all.
- **Grouping.** A reading counts in the minute its `ts_ms` falls in, `floor(ts/60000)`. Suspect rows are left out.
- **Day and minute.** `day` is the local date of the minute's start. `minute` is `(minuteStartUtc − localMidnightUtc(day)) / 60000`,
  so a 25-hour day reaches 1499.
- **Averages** are weighted by `delta_s`. `avgW`, and each part's watts, are Σ(w·Δ)/ΣΔ. `maxW` is the highest
  `total_w`. `gpu_load` and `brightness` average over the rows that have one, and are null when none does.
- **Seconds.** Σ Δ for rows with `display_on`, `user_idle`, `locked` and `on_battery`. Quality splits the seconds into
  measured, calibrated and estimated (`Quality`: Estimated 0, Calibrated 1, Measured 2).
- **Samples** is the row count.
- **Majorities.** `total_source` and `gpu_scope` take the value holding the most seconds, the lowest on a tie.
  `measured_mask` sets each bit held for at least half the minute's seconds.

`MinuteRow` mirrors `outbox_minutes`.

**Tests:**
- Σ over minutes of avgW × (measured + calibrated + estimated seconds) equals Σ raw total_w × Δ to 1e-9, a property
  test with FsCheck if the project has it, else 50 seeded random days;
- the 25-hour and 23-hour days of `Europe/London`, where a minute after the repeated hour has its right index;
- nulls;
- the ties in each majority;
- a suspect row left out.

#### C4. The report — `Sharing/Report.cs`, `Sharing/ReportJson.cs`, `Sharing/ReportBuilder.cs`

- **DTO records** mirror `valid-full.json` exactly: `ReportV1`, `ConsentDto`, `DiagnosticsDto`, `SourceDto`,
  `CrashDto`, `UsageDto`, `PowerDto`, `HardwareDto`, `CpuDto`, `GpuDto`, `MonitorDto`, `PsuDto`, `UpsDto`,
  `ProfileDto`, `TariffDto`, and `MinutesDto` with one array per column (`double?[]` for `gpuLoad` and `brightness`).
- **`ReportJson`** is a source-generated `JsonSerializerContext` in camelCase. Nulls are written, except that
  `ReportV1.Diagnostics`, `.Usage` and `.Power` use `[JsonIgnore(Condition = WhenWritingNull)]`, so a section whose
  switch is off is absent.
- **`ReportBuilder.Build(ReportInputs inputs) -> ReportV1`** copies named fields only (design §3); it never serialises a
  service object whole. `ReportInputs` is a record defined here holding:
  - the header facts and the consent;
  - the minutes;
  - the day's events;
  - the latest `ServiceStatus`;
  - the `InventoryFacts`, the `ServiceSettings` and the tariff in force;
  - whether hardware goes.
- **Rounding.** Watts `Math.Round(x, 1)`, loads and brightness 3 places, seconds 1 place.
- **The header:**
  - `windows` is `Environment.OSVersion.Version` as `major.minor.build`;
  - `arch` is `RuntimeInformation.OSArchitecture` (`x64` or `arm64`);
  - `app` is the service's informational version with `+hash` cut off.
- **Hardware:**
  - **cpu.** `model` from `InventoryFacts.CpuName`; `cores` null; `threads` from `Environment.ProcessorCount`; `tdpW`
    is the profile's override, else the facts' `CpuTdpW`, with `tdpTyped` saying which.
  - **gpus.** One item when `GpuName` is known. `vendor` from the name (NVIDIA, AMD or Radeon, Intel or Arc,
    Qualcomm or Adreno, else `other`); `discrete` from the latest reading's `DGpuPresent`; `tdpW` and `tdpTyped` as
    for the CPU.
  - **memoryGb.** `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2^30`, to 1 place.
  - **monitors,** from the status board's `MonitorStatus`:
    - `maker` and `product` parsed from the instance's second segment (`DISPLAY\GSM5B7F\…` gives `GSM` and `5B7F`,
      else null);
    - `figureFrom` mapped from where its figure came from;
    - the instance, the key and any serial are never copied.
  - **psu.** `tier` from the profile. `name` is a power supply's name from `PowerDevices`, and `ratedW` is
    `PsuEfficiency.RatedWatts(name)`, else null.
  - **ups.** The UPS's `PowerDevices` name, and `powers` from `Profile.UpsLoad` (`notSaid`, `thisPc`,
    `thisPcAndMonitors`, `more`). Null with no UPS.
  - **profile.** From `MachineProfile`, with `panelIn` null when 0.
  - **sampleIntervalS** from the settings.
  - **tariff.** The rate in force now, from `TariffRepository.Schedule()`, as price per kWh and currency, or null.
- **Diagnostics:**
  - one `SourceDto` per `SourceStatus`, `id` being its `Name`;
  - `state` follows the About page's logic (`SettingsViewModel.cs:296-319`): `notOnMachine`, `failing`, `note` or
    `working`;
  - `failures` is the day's count from the outbox;
  - `lastError` is scrubbed;
  - `device` is the graphics name for the three GPU sources, and the device name for `ups` and `power-supply`;
  - `crashes` come from the outbox.

**Tests:**
- `valid-full.json` deserialised into `ReportV1` and serialised again is `JsonNode.DeepEquals` to the file;
- a report built from fake inputs with every section passes `report-v1.schema.json` (reuse `SharingContractTests`'
  loader);
- each switch off leaves its section out;
- **privacy.** Build from inputs stuffed with `Environment.MachineName`, `Environment.UserName`, `"CONTOSO"`,
  `@"C:\Users\alice"`, `@"\\?\hid#vid_1b1c&pid_1c05#7&2d0f1a&0&0000#{…}"`, a monitor instance
  `@"DISPLAY\GSM5B7F\5&1a2b3c&0&UID4352"` and a monitor key with a serial. The serialised JSON contains none of them.

#### C5. Outbox and state — `Storage/OutboxRepository.cs`, `Service/Sharing/SharingStore.cs`

- **`OutboxRepository`:**
  - `InsertMinutes(IEnumerable<MinuteRow>)`, upserting by `start_ms`;
  - `MinuteDays()`, `Minutes(day)`, `EventDays()`, `Events(day, kind)`, `DeleteDay(day)`;
  - `DeleteMinutes()`, `DeleteEvents(kind)`;
  - `AddEvent(day, kind, json)`, and `MergeEvent(day, kind, Func<string?, string> merge)` for `usage` and `sources`;
  - `Clear()`.
- **`SharingStore`** keeps its state in settings keys:

  | Key | Holds |
  |---|---|
  | `sharing.consent` | JSON `{consent, atMs}` |
  | `sharing.id`, `sharing.key` | the install ID and key |
  | `sharing.minute` | the send minute, chosen once: random 10–359 |
  | `sharing.collected-to` | ms |
  | `sharing.last-sent` | `{atMs, bytes}` |
  | `sharing.problem` | `{text, rejected}` |
  | `sharing.backoff` | `{failures, nextMs}` |
  | `sharing.hardware-hash` | the last hardware sent |
  | `sharing.consent-pending` | a consent change the server hasn't heard yet |

  - The key is `Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))` in base64url, without padding.
  - The ID is `Guid.NewGuid().ToString("D")`.

**Tests:** round trips; the minute is chosen once and kept; `MergeEvent` adds usage counts and replaces its states,
following `UsageCounts`' rule.

#### C6. The client — `Sharing/SharingClient.cs`, `Sharing/SharingEndpoint.cs`

- **`ISharingClient`:**
  - `SendReportAsync(byte[] gzipBody, string installId, string key)`;
  - `SendConsentAsync(string installId, string key, Consent consent)`;
  - `DeleteAsync(string installId, string key)`.
- **Outcomes,** as a `SendOutcome` of `Accepted`, `Rejected(reason)`, `Gone`, `Refused(reason)` or
  `Unreachable(reason)`:
  - 200 is `Accepted`;
  - 400 and 413 are `Rejected(reason)`, with `reason` read from the `error` JSON;
  - 410 is `Gone`;
  - 401, 403, 429 and 5xx are `Refused(reason)`;
  - an HTTP exception or a timeout is `Unreachable(reason)`.
- **The HTTP client:**
  - one `HttpClient` for the service's life;
  - user agent `PowerLedger/<version>`, a 60 s timeout;
  - `Content-Encoding: gzip` on reports, and `Authorization: Bearer <key>`.
- **`SharingEndpoint`:**
  - `BuiltIn` is `https://powerledger-data.example.invalid/` until the lead sets the deployed URL (L5).
  - `Resolve()` reads the REG_SZ value `SharingEndpoint` under
    `HKLM\SYSTEM\CurrentControlSet\Services\PowerLedger\Parameters`. It uses it only when it's an absolute http or https
    URL on a loopback address, the rule `AppOptions` applies to `--update-feed`; otherwise `BuiltIn`.
  - Make the registry read injectable for tests.

**Tests** use a fake `HttpMessageHandler`: each status maps to its outcome; the headers are right; the body is gzip that
unpacks to what was built; the override is honoured only on loopback.

#### C7. The worker — `Sharing/SharingWorker.cs`, `Sharing/SendSchedule.cs`

- **Registration.** A `BackgroundService`, registered in `ServiceHost`.
- **Commands.** They arrive on a `Channel<SharingCommand>` and are handled one at a time on the worker: consent, usage,
  crash, preview, send now, delete. Each carries a `TaskCompletionSource<SharingReply>` for the pipe to await.
- **Every five minutes, and at start:**
  1. `Consent = store.Consent`. When `!AllowsAny`, make sure the outbox is empty and stop.
  2. With `Power` on, build the minutes. From `collected-to`, or the consent moment, to `cutoff`, where
     `cutoff = floor(now, 1 minute) − 2 minutes`, read the raw rows with `ts_ms < cutoff`, run `MinuteBuilder`, and
     insert. Then set `collected-to = cutoff`.
  3. With `Diagnostics` on, add each source's failures since the last tick to the day's `sources` event. A count lower
     than last time means the sensor set was rebuilt, and counts from zero. Then ingest crash files from
     `%ProgramData%\PowerLedger\Crashes`: scrub them with `ScrubNames.Here()`, add them as `crash` events, delete the
     files.
  4. Drop days more than 14 days old, their rows and events together.
  5. Post a pending consent change.
  6. When `SendSchedule.Due(...)`, send.
- **`SendSchedule`** is pure. It is due when a complete day (before today, local) waits and either:
  - now is past today's send minute and today's run hasn't happened;
  - the back-off's `nextMs` has passed;
  - send now was asked.
- **Back-off.** After a `Refused` or `Unreachable` run, `nextMs = now + [1, 2, 4, 8, 16, 24][min(failures−1, 5)]` hours.
  `Accepted` resets it.
- **Sending:**
  - oldest day first, at most 7 a run;
  - build the report with the sections switched on now;
  - put in `hardware` when its hash differs from `hardware-hash`;
  - serialise, gzip, and send.
- **After each outcome:**
  - **`Accepted`:** delete the day, and write `%ProgramData%\PowerLedger\Sent\<day>.json.gz`, keeping the newest 30. Set
    `last-sent`, clear `problem`, and update `hardware-hash` when hardware went.
  - **`Rejected`:** delete the day and set `problem` as rejected. Carry on to the next day.
  - **`Gone`:** forget everything, as a delete does.
  - **`Refused` or `Unreachable`:** set `problem` and the back-off, and stop the run.
- **Consent commands:**
  - Validate with `Consent.Validate()`.
  - The first switch turned on creates the ID and key and sets `collected-to` to now.
  - Turning off `power` deletes the unsent minutes and clears `collected-to`. Turning off `diagnostics` deletes the
    `crash` and `sources` events. Turning off `usage` deletes the `usage` events.
  - Save, then post the consent. A failure marks it `consent-pending`.
- **Usage and crash commands.** They are validated and recorded only while their switch is on, and are always
  acknowledged. A crash is scrubbed before it's stored.
- **Preview** writes `Sent\preview.json`, indented and not gzipped. It holds every section as if every switch were on,
  from the last 60 minutes of raw rows and the current status. It uses the real ID, or the zero GUID before there is one,
  and replies with the path.
- **Delete:**
  - Post `/v1/delete`.
  - On `Accepted` or `Gone`, forget: consent becomes `(ConsentText.Version, all false)`, the outbox is cleared, the ID,
    key and state keys are removed, and `Sent\*` is deleted. Reply "Your data has been deleted from the server."
  - On anything else, reply with the reason and change nothing.
- **Folders.** The `Sent` and `Crashes` folders are created under the data directory, whose ACL gives Users read.

**Tests** use a fake client and `FakeTimeProvider`:
- nothing is sent, and no HTTP is made, before consent or with every switch off;
- tonight's minute sends yesterday;
- a missed night is caught up at the next tick;
- 7 days go at most;
- the back-off steps run 1, 2, 4, 8, 16, 24, 24;
- 15-day-old data is dropped;
- `Rejected` moves on;
- `Gone` forgets;
- a withdrawal deletes its data;
- hardware goes once, then again after a change;
- preview is written with every section and passes the schema;
- delete forgets only on success.

#### C8. The pipe — `PipeHandler.cs`

Route the six requests to the worker:
- `setConsent`, `sendNow`, `deleteMyData` and `previewUpload` reply with the worker's `SharingReply`, or `ErrorReply`
  when validation fails;
- `reportUsage` and `reportCrash` reply `OkReply`, or `ErrorReply` for invalid counts or a crash.

**Tests** in `PipeHandlerTests` style: each request reaches the worker; invalid input gets `ErrorReply`.

#### C9. Status

`ServiceStatus.Sharing` carries the worker's latest `SharingStatus`, published through `StatusBoard` the way sources are.
`DaysWaiting` counts the complete days in the outbox.

**Test:** the status after a consent and after a send.

#### C10. The service's crashes

- **What is caught.** `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, and the
  catch-all in `Program.Main`.
- **Where it goes.** Each writes a `CrashReport` with component `service`, trimmed, to
  `%ProgramData%\PowerLedger\Crashes\service-<utc ticks>.json`, whatever the consent. The worker ingests or deletes it
  (C7). A crash file is not scrubbed when written, since only the machine's own users can read it; it is scrubbed when
  ingested.
- **Never throw.** Writing a crash never throws out of the handler.

**Test:** the writer called with an exception tree produces a valid trimmed report with the types outermost first.

---

### Agent A — the App

Worktree `D:\PowerLedger-m\app`, branch `plan-m/app` from `plan-m/base`. Everything is under `src/PowerLedger.App`
and `tests/PowerLedger.App.Tests`. The service's side comes from Agent C: code against the Task 0 records and the
existing `FakeLink`, never the service's classes.

#### A1. Consent view model and dialog — `Sharing/ConsentViewModel.cs`, `Sharing/ConsentDialog.xaml(.cs)`

- **View model:**
  - `Diagnostics`, `Usage`, `Power` and `Share` start from the service's consent, or all false when unanswered.
  - Setting `Power` false sets `Share` false. `CanShare => Power`.
  - Commands:
    - `AllowAll`: all four on;
    - `AllowNone`: all off;
    - `Save`: as ticked.
  - Each command sends `SetConsentRequest(ConsentText.Version, …)` through the link, closes on OK, and on an error shows
    the message and stays open.
  - `SeeWhatWouldBeSent` sends `previewUpload` and opens `Sharing/PayloadWindow.xaml(.cs)`, built in this task, on the
    returned path. It is a read-only monospaced view of one JSON file, indented, decompressing `.gz`.
  - `OpenPrivacyPolicy` opens `https://github.com/mharisjamal/PowerLedger/blob/main/PRIVACY.md` in the browser.
- **Dialog:**
  - modal and owned by the main window, following the App's theme resources;
  - the three buttons share one style and one width, in the order **Allow all**, **Allow none**, **Save choices**;
  - closing with the X sends nothing.
- **Exact wording.** Title **Help make PowerLedger better**. Intro: "PowerLedger works fully without sending anything.
  If you agree, it sends what you choose below to PowerLedger's server once a day. Nothing is sent until you choose,
  and you can change your mind at any time in Settings → Privacy."

  | Switch | One line | What's sent (expander) |
  |---|---|---|
  | **Crash and sensor reports** | When PowerLedger crashes or a sensor fails, send what went wrong and on which hardware, so it can be fixed. | Error messages and stack traces, with your user, PC and folder names removed; which sensors work or fail; the graphics card, UPS or power supply they read; PowerLedger's and Windows' versions; whether this PC is a laptop or a desktop. |
  | **Usage** | Send how PowerLedger is used, so the next version works on what matters. | How often the window opens, which pages you open, which settings you change (never what you change them to), reports exported, updates installed, days since you started, theme and display language. |
  | **Hardware and power** | Send this PC's parts and its power, minute by minute, so PowerLedger's estimates get more accurate for everyone. | The models of the processor, graphics card, monitors, power supply and UPS; memory size and the machine details in Settings; your tariff's price and currency; and for each minute, the watts of each part, the loads, the brightness, whether the screen was on, idle, locked or on battery, and which figures were measured or estimated. |
  | **Share my detailed data** (greyed until Hardware and power) | Let what Hardware and power sends be given or sold, as it is, to researchers, hardware makers and energy companies. It goes under a random ID, never your name. Copies already shared can't be taken back. | — |

  Under the switches: "Never sent: your name or your PC's name, serial numbers, files, folders or other programs. Your
  IP address is used only to find your country, then dropped." Then the links **See what would be sent** and
  **Privacy policy**.

**Tests:**
- all off to begin with;
- `Share` greyed until `Power`, and turning `Power` off clears `Share`;
- each button sends the right consent with the current version;
- an error keeps the dialog open;
- closing sends nothing.

#### A2. When it opens — `App.xaml.cs`, `Shell`

The dialog opens once per App session when all of these hold:
- the main window is shown;
- the service answered a status with `Sharing` not null;
- `Sharing.Consent.Answered` is false.

That covers an existing user's first open after updating. After the wizard's `FinishFirstRun()`, it opens straight away
under the same conditions.

**Test** with a fake status: it opens when unanswered, doesn't when answered, doesn't against an older service
(`Sharing` null), and at most once a session.

#### A3. Settings → Privacy — `Settings/SettingsView.xaml`, `Sharing/PrivacyViewModel.cs`

- **Placement.** A **PRIVACY** section between PREFERENCES and ABOUT, following the other sections' layout.
- **The switches.** The four ticks, with the same labels, and the one-liners as help text. Each change sends
  `setConsent` straight away; the section has its own message line, like the App ticks' `AppMessage`.
- **Install ID:** the ID with a **Copy** button, or "None yet" before one exists.
- **Status line:**
  - unanswered: "You haven't chosen yet.";
  - all off: "Nothing is sent.";
  - on, before any upload: "Nothing sent yet.";
  - after one: "Last sent 24 Sep 2026 · 41 KB", in the user's culture, KB rounded up;
  - a problem, not rejected: "Couldn't send: {problem}. Will try again.";
  - rejected: "Rejected by the server: {problem}".
  - When days wait, add " · {n} days waiting".
- **Buttons:** **What's been sent…** opens `SentWindow`; **Delete my data…** asks first; plus a **Privacy policy** link.
  - The delete dialog is titled "Delete my data", reads "This deletes everything PowerLedger has sent from this PC from
    the server and turns every switch off. Data already shared with others can't be recalled.", and has **Delete** and
    **Cancel**.
  - It then sends `deleteMyData` and shows the reply.
- **Refresh.** The rows follow the ten-second status refresh without overwriting a tick being sent.

**Tests:** each status maps to its line; a tick sends `setConsent`; delete asks first and sends only on **Delete**;
a refresh doesn't undo a tick in flight.

#### A4. What's been sent — `Sharing/SentWindow.xaml(.cs)`, `Sharing/PayloadWindow.xaml(.cs)`

- **SentWindow:**
  - lists `%ProgramData%\PowerLedger\Sent\*.json.gz`, newest first: the day and its size;
  - selecting one shows its JSON, decompressed and indented, read-only and monospaced;
  - **Send now** sends `sendNow`, shows the reply, and refreshes the list;
  - a missing folder shows "Nothing has been sent from this PC.".
- **Showing each file** reuses `PayloadWindow` from A1.

**Tests:** listing and ordering with a temp folder; decompression; the empty state.

#### A5. The App's crashes — `Sharing/CrashCatcher.cs`, `Sharing/CrashForwarder.cs`

- **CrashCatcher.** In `OnStartup`, hook `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException`
  and `TaskScheduler.UnobservedTaskException`, leaving the existing behaviour as it is (don't mark anything handled that
  wasn't).
- **The crash file.**
  - Each crash is written to `%LOCALAPPDATA%\PowerLedger\Crashes\app-<utc ticks>.json` as a `CrashReport` with
    component `app`, trimmed and scrubbed with `ScrubNames.Here()`.
  - Types come outermost first, down the inner exceptions, flattening an `AggregateException`.
  - Writing never throws.
- **CrashForwarder.** At start, once the service answered with `Sharing.Consent` allowing diagnostics, it sends each
  file with `reportCrash` and deletes it on OK. Files older than 7 days are deleted whatever the consent.

**Tests:** the report from a nested exception; the forwarder sends only with consent, deletes on OK, and keeps a file
when the send fails.

#### A6. Usage counts — `Sharing/UsageCounter.cs`

- **What it counts:**
  - `AppOpens` when the main window is shown;
  - `Pages[name]` on each page navigation, with the camelCase page name: find them in the shell (`now`, `history`,
    `settings`, and so on);
  - `Settings[name]` for each setting the user changes, with a camelCase name per setting, never the value;
  - `ReportsExported` when a PDF report is written;
  - `UpdatesInstalled` on **Restart to update**.
- **Flushing.** Every 15 minutes, at local midnight, and on exit, while `Sharing.Consent.Usage` is on, it sends
  `reportUsage` with the counts since the last flush and clears them on OK. With Usage off, it counts nothing.
- **States:**
  - `DaysSinceFirstRun` from a new `FirstRunAt` in `UiPreferences`, set when the wizard finishes, or on the first start
    of 0.6.0 when it's missing;
  - `Theme` as `light`, `dark` or `system`;
  - `Language` from `CultureInfo.CurrentUICulture.Name`, or `en` when that's empty.

**Tests:** counts add and clear on OK; nothing is kept when Usage is off; the day rolls at midnight; the page and setting
names match `^[a-z][A-Za-z0-9]{0,39}$`.

---

## Wave 2 — the lead

- [ ] **L1. Merge.**
  - Fast-forward or merge `plan-m/server`, `plan-m/service` and `plan-m/app` into `plan-m/base`.
  - Build, and run every .NET test with the filter, plus `npm test` in `server/`.
  - Reset each agent's worktree to the merge before any later fix round.
- [ ] **L2. Documents.**
  - `PRIVACY.md`, per design §8. The controller and contact go in once the owner confirms them; they gave
    `muhammadharis1302`.
  - README: a "Sharing data" section, and the sentence saying nothing leaves the PC replaced.
  - Main spec: §1, §8, §9 and §11.
- [ ] **L3. Review.** One whole-branch review by a reviewer subagent. Fix every finding, test-first.
- [ ] **L4. Sandbox, end to end:**
  - `sharing-feed.ps1`, an HttpListener stand-in on `127.0.0.1:8766` that logs each request and answers 200, or 410
    when told to;
  - the registry override `SharingEndpoint`;
  - checks, driving the service through its pipe from PowerShell:
    - no request before consent;
    - `setConsent` all on, then `sendNow`, and a report arrives with every section, gzip, and a bearer key;
    - the stand-in's 410 turns every switch off;
    - a preview file appears.
  - Plus the existing 61 checks.
- [ ] **L5. Deploy.** It needs the owner:
  - The owner makes the Cloudflare account and runs `npx wrangler login` in `server/`.
  - The lead then runs:
    - `npx wrangler d1 create powerledger-index`, and sets the id in `wrangler.toml`;
    - `npx wrangler r2 bucket create powerledger-data`;
    - `npx wrangler d1 migrations apply powerledger-index --remote`;
    - `npx wrangler deploy`.
  - The owner runs `tools/set-admin-token.ps1`.
  - The lead:
    - sets `SharingEndpoint.BuiltIn` to the deployed URL, with a test that it's `https` on `workers.dev`;
    - smoke-tests: a fixture under a fresh ID gives 200, `/admin/stats` counts it, a delete gives 200, and the same ID
      gets 410.
- [ ] **L6. Release 0.6.0.**
  - Bump `Directory.Build.props`, build the installers, and run the Sandbox again.
  - Run `release.ps1`, with notes that lead with the change: what is sent, only by choice, and how to delete it.
  - Check CI.
- [ ] **L7. Results.** Fill in the section below, and update the memory.

## Results

Filled in after the release.
