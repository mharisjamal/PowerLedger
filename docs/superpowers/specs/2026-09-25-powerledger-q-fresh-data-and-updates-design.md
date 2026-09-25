# PowerLedger Plan Q: fresher data, history for new users, enforced updates (0.9.0)

Owner's decisions, 2026-09-25:

| Question | Decision |
|---|---|
| History already on a PC | Sent only by people who agree from 0.9.0 on. Nobody who agreed before is asked again. |
| How much history | All of it, as hourly totals (`samples_1h`, kept forever on the PC). No per-minute history. |
| Updates | Both: the service installs new versions itself, silently, and the server enforces a minimum version. |
| How often data is sent | Every hour ("today so far", replacing the earlier copy), then the complete day just after midnight. |

Nothing in this plan sends anything a user hasn't agreed to. Existing consents keep meaning exactly what they meant.

## 1. Hourly sending

- **Service.** Besides the complete days it sends now, the sharing worker sends *today so far* once an hour, at the
  install's send minute modulo 60 (so installs spread over the hour). It's built exactly like a complete day's report from
  the outbox minutes and events of today, with `"complete": false`. Today stays in the outbox (not deleted, not closed).
  When the day ends, the complete day goes as now, with `"complete": true`, and closes the day.
- **Complete days go sooner.** `SharingStore.LastSendMinute` 359 → 59: a complete day goes between 00:10 and 00:59.
- **Sent copies.** A partial upload writes its copy to the same `Sent\<day>.json.gz` a complete one does (the complete one
  replaces it), so "What's been sent" shows today's latest and the 30-copy limit counts days.
- **Server.** Report schema v1 gains an optional boolean `complete` (absent means true, so older apps are unchanged). The
  `reports` table gains `complete INTEGER NOT NULL DEFAULT 1` (migration 0006). The upsert on `(install_id, day)` already
  replaces a day. `MAX_REQUESTS_PER_DAY` for `/v1/report` 20 → 60. `/admin/list` and `tools/export.mjs` carry `complete`.
- **Words.** Consent dialog: "once a day" → "every hour". PRIVACY.md "How often": "every hour, today so far, and each day
  once more when it is complete".

## 2. History for new users (consent version 2)

- **Consent.** `ConsentText.Version` 1 → 2. `Consent.Answered` is true for version 1 or 2 (a version-1 answer stays
  answered, so nobody is asked again); `Validate` accepts 1 and 2. The dialog's Allow all records version 2. Only version 2
  with Hardware and power on sends history. A version-1 PC that later turns Hardware and power off and on again records
  version 2 then (it is answering again on the new wording), and so sends history then.
- **Dialog words** (Hardware and power line): add "…including the hourly totals already on this PC". PRIVACY.md's
  Hardware and power row adds: "When you agree, the hourly totals already on your PC are sent once too."
- **Service.** After a version-2 answer with Power on, the sharing worker sends every `samples_1h` row from before the
  moment of consent once, oldest first, in chunks of at most 31 days (one request each, gzip JSON, well under 1 MB). Progress
  is kept in `SharingStore` (`HistoryThroughMs`), so a restart carries on. Turning Power off stops it and forgets progress.
  Delete my data deletes it on the server with everything else.
- **History upload** `POST /v1/history`, gzip, `Authorization: Bearer <install key>`:
  `{ schema: "history-v1", installId, app, consent, utcOffsetMinutes, hours: [ { t, <the samples_1h columns as numbers> } ] }`
  where `t` is the hour's start in UTC ms. The schema lists the columns exactly (the implementer reads `samples_1h` in
  `src/PowerLedger.Storage/Schema.cs` and mirrors each numeric column).
- **Server.** New table `histories(install_id, from_ms, to_ms, received_at, bytes, r2_key, PRIMARY KEY(install_id, from_ms))`,
  bodies stored the way report bodies are (`report_bodies` / R2) under `history/v1/<installId>/<from_ms>.json.gz`. Same
  checks as `/v1/report` (install key, size limits, gzip, schema, consent version 2 with power on, request count). Delete
  and retention (3 years from `to_ms`) cover it. `/admin/list?kind=history` pages it; `tools/export.mjs` writes
  `hours.csv` (`pc,country,t,<columns>`), pseudonymised like the rest.

## 3. Minimum version (server-enforced)

- **Server.** `wrangler.toml` var `MIN_APP_VERSION` (starts at `0.9.0` once 0.9.0 is out; `0.0.0` until then). Every
  service request carries header `X-PowerLedger-Version: X.Y.Z`; `/v1/report` and `/v1/history` also check the body's
  `app`. Lower than the minimum → `426` with `{ "error": "update required", "minVersion": "X.Y.Z" }` and nothing stored.
  `GET /v1/app-policy` → `{ "minVersion": "X.Y.Z" }` (no auth, cacheable 5 minutes).
- **Service.** Reads `/v1/app-policy` hourly (and at start) and on any 426. Publishes `UpdateRequired(minVersion)` in its
  status to the App. While the running version is below the minimum it sends nothing (no backoff noise), and the update
  worker (§4) treats the update as urgent: install at once.
- **App.** While status says the running version is below the minimum, a blocking window covers the main window in both
  looks: "PowerLedger needs an update" / "This version is out of date and has stopped sending data. Update to keep using
  PowerLedger." Buttons: **Update now** (asks the service to install now, or falls back to the App's own setup flow) and
  **Close PowerLedger**. The tray stays.

## 4. Silent updates by the service

- **Signed releases.** The owner holds an ECDSA P-256 key in `%USERPROFILE%\.powerledger\release-signing.pem`, made once by
  `scripts/new-release-key.ps1` (prints only the public key; the private key never leaves that file). `release.ps1` signs
  each installer as the UTF-8 message `PowerLedger|<version X.Y.Z>|<file name>|<SHA-256, lowercase hex>`, so a signature
  can't be carried over to another release or file, and uploads `PowerLedger-X.Y.Z-signatures.json` (`{ "<file name>": "<base64 DER
  signature>" }`) with the installers. The public key is compiled into the service (`ReleaseKey.cs`). A release whose
  installer has no valid signature is never installed by the service (the App's click-to-install flow still works as now).
- **Shared feed.** `GitHubReleaseFeed`, `Release` and the download/verify code move from the App to a project both use (or
  are linked), unchanged in behaviour. The feed also finds the signatures asset.
- **Update worker (service).** Checks hourly (and 2 minutes after start). Downloads the installer for its architecture to
  `%ProgramData%\PowerLedger\Updates`, a folder the service creates with an ACL of SYSTEM and Administrators only (so no
  user can swap the file). Verifies size, SHA-256 and the signature while holding the file open, and never installs a
  version not newer than its own.
- **When it installs.** At once if the update is required (§3), if no user is signed in at the console, or if the App's
  main window isn't showing. Otherwise when the window has been hidden or the user idle for 5 minutes, and at the latest
  6 hours after the download, with a tray notice a minute before ("PowerLedger updates in a minute"). On a metered
  connection it waits for an unmetered one for up to 24 hours, then downloads anyway.
- **The App's window state.** A new pipe message from the App, `uiState { windowVisible }`, sent when it changes.
- **Running setup.** The service starts the installer detached with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1
  /SERVICEUPDATE=1 /LOG=<ProgramData\PowerLedger\Updates\setup-X.Y.Z.log>`. With `/SERVICEUPDATE=1` the installer skips its
  `[Run]` App launch (it would start as SYSTEM). Before starting setup the service writes `relaunch.json`
  (`{ version, windowWasVisible }`) in the Updates folder; the new service on start reads it, and launches the App in the
  console user's session with `WTSQueryUserToken` + `CreateProcessAsUser` (`--after-update`, tray only unless the window
  was visible), then deletes the file.
- **The App's own updater** stays as the fallback. When the service reports that it handles updates, the card shows
  "Installing automatically" instead of Restart to update. Settings' "Check automatically" tick box is removed: updates
  are always on.

## 5. Also in 0.9.0

- The approval-test flake fix left uncommitted in `D:\PowerLedger-n\flake-approval` (fix/approval-flake) is finished,
  root-caused with evidence, and merged.
- What's new 0.9.0, PRIVACY.md (including its stale "Allow all, Allow none and Save choices" line: the dialog has two
  buttons).

## Testing

Unit tests for every new rule (schedules, consent versions, history chunking, signature verification, policy parsing,
install timing, 426 handling); Worker tests for the new endpoints, limits, delete and retention; the Sandbox e2e extended
with a service update from a local 0.9.0 to a local 0.9.1 through `--update-feed`, checking the App comes back in the
user's session.
