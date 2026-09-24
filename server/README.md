# PowerLedger's data server

A Cloudflare Worker, `powerledger-data`, written in TypeScript. It stores the daily reports users choose to send
(in R2, or in D1 until R2 is enabled on the account — see Storage, below), and indexes them in a D1 database for
the owner's export and admin tools. See `docs/superpowers/specs/2026-09-24-powerledger-data-sharing-design.md` §6
for the design.

## What it does

- `POST /v1/report` accepts one PC's day: gzip-encoded, at most 1 MB as sent and 8 MB unpacked (a zip bomb is
  cancelled mid-stream), checked against `schema/report-v1.schema.json` and a hand-written minutes check. A valid
  report is stored as sent, at `reports/v1/<installId>/<day>.json.gz`, and indexed in D1. The first request from an
  install id records its key (trust on first use); a later request with a different key is refused.
- `POST /v1/consent` records an install's four switches (diagnostics, usage, power, share). `POST /v1/delete`
  deletes everything the server holds for an install and leaves a tombstone, so any later request for that id is
  refused.
- `GET /admin/stats`, `GET /admin/list` and `GET /admin/object`, all behind `ADMIN_TOKEN`, are for the owner only:
  install and upload counts, a paged list of stored reports, and streaming one report's bytes back out.
- A daily cron (`retention.ts`) drops reports whose day is more than 3 years old, and request counts more than 2
  days old.

## Households

`src/households/` holds the households routes (`docs/superpowers/specs/2026-09-24-powerledger-households-design.md`
§5 to §8). The server keeps membership, public keys and sealed batches; never a household key, a PC's name or a row.

- **Signed requests.** Every household and account request carries `X-PL-Device`, `X-PL-Time` (unix seconds, within
  300 s) and `X-PL-Signature`: base64url ECDSA P-256 (r ‖ s) over `METHOD\npath?query\ntime\nhex SHA-256(body)`, the
  path and query as sent. The headers and the signer (member row or session) are checked before the body is read.
  A signature is taken once, and a replay doesn't count against the PC's 1000 requests a UTC day. On household
  routes, no member row and a bad signature get the same 401; a removed PC gets 410 ("This PC was removed from the
  household."), and only once its signature is good. Keys are taken only in canonical SPKI (DER, point uncompressed).
- **Households.** `POST /v1/households` (`{"id","sign","dh"}`, signed by that key); `GET …/{hid}/members`;
  `POST …/{hid}/members` (`{"sign","dh","proof"}`, where the proof is the joining PC's ECDSA P-256 signature, P1363,
  over UTF-8 `powerledger join|{hid}|{sign}|{dh}` with the keys as posted: 400 without one, 403 when it doesn't
  verify; a current member with the same keys is 200, with another dh key 409); `DELETE …/{hid}/members/{device}`
  (the household ends with its last member). At most 16 members.
- **Keys.** The server keeps each household's epoch, 1 at creation. `POST …/{hid}/keys`
  (`{"epoch","envelopes":[{"device","body"}]}`) takes only the current epoch + 1, which then becomes current (409
  otherwise, a retry included); `GET …/{hid}/keys/{epoch}` gives `{"epoch","from","body"}`, the caller's own.
- **Removing a PC**, or its leaving, also deletes the recovery of every account linked to the household; unlinks the
  accounts the removed PC was signed in as, if linked to this household, and ends those sessions of it; and clears
  its request to join. Accounts linked to another household are left alone.
- **Batches.** `POST …/{hid}/batches` (`{"device","epoch","seq","body"}`, at most 1 MB, `seq` the sender's own); a PC
  posts at most 200 batches and 5 MB of them a UTC day (429), and the server takes at most 2 GB a day in all (503,
  "The server is busy; try again later."). `GET …/{hid}/batches?after=&limit=` gives
  `{"items":[{"seq","device","epoch","body"}],"next","more"}`: the others' batches as posted, `next` the cursor to
  send as `after` next time, `more` when another page waits.
- **Meetings**, for pairing by code: `PUT`/`GET /v1/meetings/{id}/{adder|joiner|answer|welcome|joined}`, unsigned;
  each slot written once, 8 KB at most, for 10 minutes from the meeting's first `PUT`. Every `PUT` clears ended
  meetings; starting one has its own per-address limit (`MEETING_LIMIT`, 10 a minute, 429), and at most 5000 are live
  (503).
- **Sign-in.** `POST /v1/auth/signin` (`{"provider","idToken","nonce","sign","dh"}`) checks the ID token (RS256 only;
  issuer, a Microsoft key's own issuer, audience with azp for several, expiry, issue time, not-before) and gives
  `{"session","account","householdId","hasRecovery"}`, `account` being the account's opaque ID. The posted `nonce` is
  a salt: the token's nonce claim must be base64url(SHA-256(UTF-8(`<device ID>:<salt>`))) for the PC that signed the
  request, so a token only signs in the PC that asked for it. A Microsoft account is its tenant and subject.
- **Accounts**, with `Authorization: Session <token>` as well as the signature: `POST /v1/account/household`
  (`{"householdId"}`, required, a household the PC is a current member of); `POST /v1/account/requests` (ask to join:
  16 waiting a household, 2 an account, lapsing after 7 days); `PUT /v1/account/recovery` (`{"body","verifier"}`, a
  32-byte verifier of which only SHA-256 is kept, with the household's current epoch) and `GET` (`{"householdId",
  "epoch","body"}`); `POST /v1/account/recover` (`{"verifier"}`: 403 when it isn't the one put, 409 when the epoch has
  moved on since); `POST /v1/auth/signout` (the session and the PC's own requests); `DELETE /v1/account`.
- **Requests, for members.** `GET …/{hid}/requests` gives `[{"device","account","sign","dh","created"}]`;
  `POST …/{hid}/requests/{device}/approve` (`{"epoch","body"}`, the current epoch only; an envelope the PC already
  has there is never overwritten, 409); `DELETE …/{hid}/requests/{device}` denies.
- **Address limit.** Every household, account, sign-in and meeting route, and the reports ones, are behind
  `ADDRESS_LIMIT` (60 a minute), an IPv6 address counted by its /64.
- **Retention.** The daily cron also drops batches past 90 days, ended meetings, join requests past 7 days, per-PC
  counts and daily totals past 2 days and seen signatures past 10 minutes. Backlogs are worked through for up to
  20 s a run, and each part runs on its own, so one failing doesn't stop the others.

Sign-in needs the public client IDs in `wrangler.toml`'s `[vars]`, `MS_CLIENT_ID` and `GOOGLE_CLIENT_ID`; while one is
empty, that provider's sign-in answers 503. `wrangler.toml` also binds `MEETING_LIMIT`. Migrations
`0003_households.sql` and `0004_accounts.sql` go out with the usual
`npx wrangler d1 migrations apply powerledger-index --remote`.

## Storage

Report bodies live in D1 (`report_bodies`) until R2 is enabled on the account — comfortably inside the free 5 GB
D1 limit, since each body is at most 1 MB. `src/store.ts` is the one place that reads, writes and deletes bodies;
a body already in D1 stays readable once R2 takes over, with no migration step.

To switch: enable R2 on the account, then

```
npx wrangler r2 bucket create powerledger-data
```

uncomment the `[[r2_buckets]]` block in `wrangler.toml`, and

```
npx wrangler d1 migrations apply powerledger-index --remote
npx wrangler deploy
```

## Running the tests

```
npm ci
npm test
```

On Windows, a private `TEMP` must be a plain Windows path (`TEMP="$(cygpath -w "$TEMP/x")"` in Git Bash): with a mixed
one such as `C:\…\Temp/x`, workerd's SQLite can't make the temp file a larger D1 transaction needs, and it fails with
`SQLITE_CANTOPEN`.

`npm test` runs the Worker's own tests (`vitest`, against a local Miniflare D1 and R2 — no network, no Cloudflare
account needed) and the export tool's pure-function tests (`node --test`, plain Node).

## One-time setup, at deploy

These are the lead's commands (design §6, plan L5), run once when the Worker is first deployed:

```
npx wrangler login
npx wrangler d1 create powerledger-index          # then set the returned id in wrangler.toml
npx wrangler d1 migrations apply powerledger-index --remote
npx wrangler deploy
```

R2 isn't part of first deploy — the account doesn't have it enabled yet, and the Worker runs on D1 alone until it
does (see Storage, above).

The owner then runs `tools/set-admin-token.ps1` themselves. It creates `%USERPROFILE%\.powerledger\admin.json`
(a token and a salt, 32 random bytes each) the first time, or reuses it if it already exists, and sets `ADMIN_TOKEN`
as a Worker secret. Nobody else ever sees the token.

## Exporting

```
node tools/export.mjs --url https://<the deployed worker> --from 2026-09-01 --to 2026-09-30 [--shared] --out ./export
```

Reads the token and salt from the owner's `admin.json`, pages through `/admin/list`, downloads and gunzips each
report from `/admin/object`, and writes, under `--out`:

- `reports.ndjson` — one report a line, minutes kept columnar (the arrays as they were sent);
- `minutes.csv` — one row a minute: `pc,day,country,chassis,arch,t,<every minutes column>`;
- `hardware.ndjson` — `pc`, `day` and `hardware`, for reports that have hardware.

Every report's `installId` is replaced with `pc`, the first 16 hex characters of HMAC-SHA256(salt, installId), so a
buyer can follow one PC across a dataset but can never recover its real id. `--shared` keeps only installs whose
current consent has `share` on.

## Where the secrets live

`ADMIN_TOKEN` and the salt used for `pc` live only in `%USERPROFILE%\.powerledger\admin.json`, on the owner's own
PC. They are never committed, logged, or sent anywhere but Cloudflare (the token, as a Worker secret, by
`set-admin-token.ps1`).
