# Opt-in data sharing: consent, collection and the data server

2026-09-24. The owner wants the standard consent choice users know from other software, and the data of those who agree
collected in one place. They chose all four purposes: making estimates more accurate, bugs and sensor health, usage
statistics, and publishing or selling the data, with detailed rows (not only totals) shareable and sellable. The server is
Cloudflare. This reverses the promise that all data stays on the machine for the users who opt in, and only for them:
PowerLedger still works fully with nothing sent, and nothing is sent until a user says yes.

## 1. The four switches

| Id on the wire | Shown as | What it sends |
|---|---|---|
| `diagnostics` | Crash and sensor reports | crashes, and which sensors work or fail on which hardware (§3) |
| `usage` | Usage | days used, pages opened, settings changed by name, never by value |
| `power` | Hardware and power | each part's model, and minute-by-minute watts per part with loads, brightness and states, marked measured or estimated; tariff price, currency and CO₂ factor |
| `share` | Share my detailed data | nothing more: it lets what `power` sends be given or sold, as it is, to researchers, hardware makers and energy companies |

- **Every switch is off until the user turns it on.** `share` needs `power`: it can't be turned on alone, and turning
  `power` off turns `share` off.
- **The consent text has a version**, `ConsentText.Version`, starting at 1. Consent counts only while the version the
  user answered is the current one. When the wording changes materially the version goes up, nothing is sent, and the
  user is asked again.
- **Consent covers the machine**, as the data does: it lives in the service's settings, whoever gives it.
- **Withdrawing is immediate.** Turning a switch off deletes that switch's data not yet sent and tells the server at
  once (§4), so a withdrawn `share` stops being exported from that moment.
- **Nothing from before consent is sent.** Collection starts at the moment the user says yes.

## 2. Where the user decides

- **New installs:** a wizard step, "Help make PowerLedger better", before the last step.
- **Existing installs, and after a version change of the text:** a dialog the first time the main window opens in each
  App session, until it is answered. **Allow none** is an answer; closing the dialog is not, and sends nothing.
- **The dialog and the wizard step** say that PowerLedger works fully without sending anything, then list the four
  switches, each with its one-line description and a **What's sent** expander giving the exact fields. **Allow all**,
  **Allow none** and **Save choices** are the same size and style. **See what would be sent** opens the payload the next
  upload would carry, built now. **Privacy policy** opens `PRIVACY.md` on GitHub.
- **Settings → Privacy** holds the same switches, saving themselves like the rest of Settings, and:
  - the install ID, copyable, for questions to the owner;
  - a status line: "Nothing sent yet", "Last sent 24 Sep 2026 · 41 KB", "Couldn't send: <reason>. Will try again.",
    or "Rejected by the server: <reason>";
  - **What's been sent**: the last 30 uploads, each opened as the JSON that went, with **Send now**;
  - **Delete my data**, which asks first: "This deletes everything PowerLedger has sent from this PC from the server and
    turns every switch off. Data already shared with others can't be recalled."

## 3. What each switch sends

Only the fields below are ever built into an upload. The builder copies named fields into a new object; it never
serialises an existing object whole, so a field added to a model later can't leak.

- **Every upload's header:** schema version, install ID, app version, Windows build (`10.0.26200`), architecture
  (`x64`/`arm64`), chassis (laptop/desktop), consent (text version and the four switches), the local day (`YYYY-MM-DD`)
  and its UTC offset in minutes. The server adds the country, two letters, from Cloudflare's own lookup of the address
  the upload came from, and keeps nothing else of that address.
- **`diagnostics`:**
  - each sensor source by stable id (`cpuEnergy`, `battery`, `cpuLoad`, `nvidia`, `amd`, `arc`, `gpuLoad`,
    `brightness`, `displayState`, `ups`, `psu`): its state at upload (working, not on this machine, failing, note), how
    many times it failed that day, its last error, and the model it read (a graphics card, UPS or supply name);
  - each crash since the last upload, of the App or the service: when, which, version, exception type chain, message and
    stack trace; at most 20 a day, each at most 16 KB.
- **`usage`:** the App's opens, the pages opened by name with counts, the settings changed by name with counts, reports
  exported, updates installed, days since the first run, the App's theme and display language.
- **`power`:**
  - **hardware**, in the first upload and whenever the inventory changes: processor model, cores and threads, TDP
    (detected or typed); each graphics adapter's vendor, model, memory and whether it is discrete; memory size; each
    drive's kind and size; each monitor's maker code, product code, model name, size, resolution, refresh rate, HDR,
    whether it has its own plug and is counted, and its figure with where that came from; the supply's tier, rated watts
    and name when it reports one; a UPS's model and what the user said it powers; the laptop battery's design capacity;
    the machine profile's chassis, fans, extras and panel size; the sample interval; tariff price, currency and CO₂
    factor.
  - **minutes**, columnar (one array per field, all the same length): the minute from local midnight (0–1499, so a
    25-hour day fits), average and highest total watts, average watts of each part (processor, graphics, display, memory,
    storage, board, extras, monitors, supply losses, unattributed), average processor load, graphics load and brightness,
    seconds with the display on, idle, locked, on battery, measured, calibrated and estimated, the sample count, the
    source of the total that minute (battery, UPS, supply wall, supply output, model), the graphics scope, and a mask of
    the parts measured (1 processor energy meter, 2 graphics library, 4 total from a device). Watts to 0.1, loads and
    brightness to 0.001, seconds to 0.1.
- **Never sent:** the PC's name, the user's or domain's name, any serial number, Windows device instance IDs or paths,
  file paths, other programs or window titles, network addresses, and any location finer than the country. Crash text and
  error text are scrubbed before storing: the user profile path becomes `%USERPROFILE%`, the user, machine and domain
  names become `<user>`, `<machine>` and `<domain>`, device paths (`\\?\…`) become `<device>`, PnP instance IDs `<id>`,
  e-mail addresses `<email>` and IP addresses `<ip>`.

## 4. The collector, in the service

The service owns the data, runs when nobody is signed in, and holds the machine-wide consent, so it builds and sends every
upload. The App passes it what only the App knows over the pipe, as it does monitor readings.

- **Three new columns on `samples_raw`**, `total_source`, `gpu_scope` and `measured_mask`, written with each reading so
  a minute can say what was measured. Rows from before hold 0 (model, nothing measured, board).
- **Outbox.** Every hour, while `power` is on, the minutes completed since the last run (never before the consent moment)
  are built from `samples_raw` into a table `outbox_minutes`; raw readings may be kept as little as 24 hours, so an hourly
  build never finds them gone. Crashes and sensor failures while `diagnostics` is on, and the App's usage counts while
  `usage` is on, go into `outbox_events` by local day.
- **Sending.** Once a day at a random minute between 00:10 and 05:59 local, chosen once per install, or at the first
  hourly tick after it while the PC is on, each complete local day in the outbox is sent oldest first, at most 7 a run, as
  one `POST /v1/report` per day: gzip JSON, at most 1 MB, `Authorization: Bearer <install key>`, user agent
  `PowerLedger/<version>`.
  - **200:** that day leaves the outbox, and a copy is kept in `%ProgramData%\PowerLedger\Sent\<day>.json.gz`, newest 30.
  - **400 or 413:** the day is dropped and the status line says the server rejected it, so a bad day can't block the rest.
  - **410:** the server has deleted this install. Every switch goes off, the outbox is emptied, the ID and key are
    forgotten.
  - **403, 429, 5xx or no network:** kept, and tried again at the next hourly tick after 1, 2, 4, 8, 16 then 24 hours.
    A day more than 14 days old is dropped unsent.
- **Identity.** On the first switch turned on, the service makes an install ID (random GUID) and an install key (32
  random bytes, base64url), stored in its database. The key never crosses the pipe and is sent only to the server.
- **Consent changes** are posted at once to `/v1/consent` when an ID exists. **Delete my data** posts `/v1/delete`; on
  success the service does what a 410 does; on failure it says so and changes nothing.
- **The endpoint** is built in (`https://powerledger-data.<account>.workers.dev`), overridden for tests the same way the
  update feed is.
- **Uploads come from the service**, so they go direct or through the machine-wide WinHTTP proxy. A PC that reaches the
  internet only through a proxy set for the user doesn't send.

## 5. The App's part

- **New pipe requests:**
  - `setConsent` with the text version and the four switches: checked (`share` needs `power`, version must be current),
    stamped, saved;
  - `reportUsage` with the day's counts, sent every 15 minutes and on exit, ignored unless `usage` is on;
  - `reportCrash` with an App crash;
  - `previewUpload`, which writes what would be sent into `Sent\preview.json` and returns its path;
  - `sendNow` and `deleteMyData`, which return what happened.
- **`ServiceStatus` gains `Sharing`:** the consent version answered, the four switches, the install ID, the last
  upload's time and size, the last error and the days waiting.
- **Crashes.** The App catches `DispatcherUnhandledException`, `AppDomain.UnhandledException` and
  `TaskScheduler.UnobservedTaskException` into `%LOCALAPPDATA%\PowerLedger\Crashes\*.json`. At its next start it passes
  each to the service while `diagnostics` is on, and deletes them once passed or after 7 days. The service catches its
  own the same way into `%ProgramData%\PowerLedger\Crashes`.

## 6. The server

A Cloudflare Worker, `powerledger-data`, in TypeScript under `server/`, with an R2 bucket for the uploads and a D1 database
for the index.

- **`POST /v1/report`:**
  - Limits: at most 1 MB as sent, and at most 8 MB unpacked, counted while decompressing so a zip bomb stops early.
  - Checks: the header, hardware, diagnostics and usage against `server/schema/report-v1.schema.json`, where unknown
    fields are refused. The minutes by a hand-written check: equal lengths, minutes rising and unique within 0–1499,
    every number finite and in range (watts 0–5000, loads and brightness 0–1, seconds 0–60).
  - The day must fall between 15 days back and tomorrow.
  - The body is stored as it came, `reports/v1/<install>/<day>.json.gz`, with the country and the time received in the
    object's metadata and the index. A day sent again replaces the first.
  - **Auth.** The first report of an unknown install records the SHA-256 of its key; a later one with another key gets
    403.
  - **Rate limits.** Per address through Workers' rate-limiting binding, and at most 20 requests a day per install,
    counted in D1.
- **`POST /v1/consent`** records the switches. **`POST /v1/delete`** deletes the install's objects and index rows and
  leaves a tombstone, so any later request for that install gets 410.
- **Owner-only, behind `ADMIN_TOKEN`:**
  - `GET /admin/stats`: installs, uploads by day, switches.
  - `GET /admin/list?from=&to=&shared=`: the objects in a range, only installs whose current consent has `share` when
    asked for it.
  - `GET /admin/object?key=`: streams one object as stored.
- **Exporting.** `server/tools/export.mjs`, run by the owner, lists, downloads and unpacks, then writes NDJSON and a
  minutes CSV. Each install ID is replaced by an HMAC of it under a salt kept only on the owner's PC, so a buyer can
  follow one PC across a dataset but can't act as it.
  `server/tools/set-admin-token.ps1`, also run by the owner, makes the token and the salt, keeps them in the owner's
  profile and hands the token to `wrangler secret put`.
- **Retention.** A daily cron deletes objects and index rows whose day is more than 3 years old.
- **Cost.** A day's upload is about 40 KB. A thousand PCs use about 1.2 GB of R2 a month, inside the free 10 GB, and
  30,000 requests a month, far inside the free daily 100,000. The free plan's 10 ms CPU limit per request is the tight
  one. Storing the body as sent, with no recompression, keeps a report inside it. If reports start failing on CPU, the
  $5 Workers Paid plan lifts it.

## 7. Retention and rights

- **On the PC:** unsent data at most 14 days; copies of what was sent, the newest 30.
- **On the server:** 3 years from the day the data describes, or until **Delete my data**.
- **Rights.** A user can see what was sent in **What's been sent**, delete it all from the App, and ask the owner for
  anything else by e-mail, quoting the install ID. Copies already given or sold can't be recalled, which the switch
  says.

## 8. Documents

- **`PRIVACY.md`** at the repository's root covers:
  - who is responsible and how to reach them;
  - each switch and what it sends;
  - why each is sent, on the basis of consent;
  - who receives it: Cloudflare as processor; researchers, hardware makers and energy companies for `share`;
  - that Cloudflare may process it in any country;
  - how long it is kept, and the rights in §7;
  - how changes are announced.
- **The owner confirms the controller's name and contact address before release.** They gave `muhammadharis1302`.
  The policy isn't legal advice, and should be reviewed before the first sale.
- **The README** gets a "Sharing data" section, replacing the statement that nothing leaves the PC.
- **The main design** changes in four places: §1 drops telemetry from what isn't built, §8 lists the new requests,
  §9 adds Settings → Privacy, and §11 replaces "all data stays on the machine".

## 9. Tests

- **Contracts:** each new request round-trips; `setConsent` refuses `share` without `power` and an old version.
- **Service:**
  - Minutes: Σ minute watts × seconds equals the raw integral, and a 25-hour day is handled.
  - The outbox survives a restart.
  - The schedule:
    - the random minute is kept;
    - a missed night is caught up after sleep;
    - at most 7 days go per run;
    - the back-off steps follow each other;
    - a day past 14 is dropped.
  - Each switch off leaves its section out; all off makes no HTTP request at all.
  - Withdrawal deletes that switch's unsent data.
  - A 410 and a delete forget the ID.
  - Scrubbing: an inventory and crash stuffed with the machine name, user name, domain, profile path, a HID path and a
    PnP instance ID produce a payload containing none of them.
- **App:**
  - The dialog: all off at first, `share` greyed until `power`, the three buttons equal, closing sends nothing.
  - The wizard step is there.
  - The Privacy section saves itself.
  - A crash is written and passed on.
  - Usage counts add up.
- **Contract between the two sides:** shared fixtures in `server/test/fixtures/`. The .NET test checks that a payload
  it builds passes `report-v1.schema.json` (JsonSchema.Net); the Worker's tests accept the same fixtures and refuse
  the broken ones.
- **Worker:** vitest with the Workers pool. It accepts good reports and refuses on schema, size, range and zip bomb; it
  checks the key on first use and gives 403 on a mismatch; a day sent again replaces the first; consent is recorded;
  delete leaves a tombstone and 410; admin needs its token; the cron deletes what is past retention.
- **Sandbox:**
  - the service posts to a stand-in on 127.0.0.1;
  - nothing goes before consent;
  - Allow all then Send now sends every section;
  - a stand-in 410 turns every switch off.

## Honest limits

- **Anyone can post made-up data** under a new random ID. Checks, limits and plausibility ranges narrow it, but no
  report is proven to come from a real PC. Analysis should prefer medians and the minutes marked measured.
- **The opt-in rate can't be known,** because a "no" sends nothing. Downloads against installs reporting is the nearest
  stand-in.
- **On a shared PC,** whoever answers first answers for the machine.
- **A proxy set only for the user** isn't used, because uploads come from the service.
- **Nothing here gives anything back yet.** Figures learned per model, shipped in updates, and "your PC against similar
  PCs" are Plan N, once enough PCs report.
