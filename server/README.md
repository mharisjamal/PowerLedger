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
  path and query as sent. A signature is taken once, and a PC makes at most 1000 requests a UTC day.
- **Households.** `POST /v1/households` (`{"id","sign","dh"}`, signed by that key); `GET`/`POST …/{hid}/members`;
  `DELETE …/{hid}/members/{device}` (the household ends with its last member); `POST …/{hid}/keys`
  (`{"epoch","envelopes":[{"device","body"}]}`, once an epoch, each epoch later than the last) and
  `GET …/{hid}/keys/{epoch}` (`{"epoch","from","body"}`, the caller's own). At most 16 members.
- **Batches.** `POST …/{hid}/batches` (`{"device","epoch","seq","body"}`, at most 1 MB, `seq` the sender's own);
  `GET …/{hid}/batches?after=&limit=` gives `{"items":[{"seq","device","epoch","body"}],"next","more"}`: the others'
  batches as posted, `next` the cursor to send as `after` next time, `more` when another page waits.
- **Meetings**, for pairing by code: `PUT`/`GET /v1/meetings/{id}/{adder|joiner|answer|welcome}`, unsigned and behind
  the address limit; each slot written once, 8 KB at most, for 10 minutes from the meeting's first `PUT`.
- **Sign-in.** `POST /v1/auth/signin` (`{"provider","idToken","nonce","sign","dh"}`) checks the ID token against the
  provider's JWKS and gives `{"session","householdId","hasRecovery"}`. With `Authorization: Session <token>` as well
  as the signature: `POST /v1/account/household` (link), `POST /v1/account/requests` (ask to join),
  `PUT`/`GET /v1/account/recovery` (`{"body","verifier","epoch"}`; `GET` never gives the verifier),
  `POST /v1/account/recover` (`{"proof"}`: HMAC-SHA256 of the device ID under the verifier), `POST /v1/auth/signout`
  and `DELETE /v1/account`. Members see and approve waiting PCs at `GET …/{hid}/requests` and
  `POST …/{hid}/requests/{device}/approve` (`{"epoch","body"}`).
- **Retention.** The daily cron also drops batches past 90 days, ended meetings, join requests past 7 days, per-PC
  request counts past 2 days and seen signatures past 10 minutes.

Sign-in needs the public client IDs in `wrangler.toml`'s `[vars]`, `MS_CLIENT_ID` and `GOOGLE_CLIENT_ID`; while one is
empty, that provider's sign-in answers 503. Migrations `0003_households.sql` and `0004_accounts.sql` go out with the
usual `npx wrangler d1 migrations apply powerledger-index --remote`.

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
