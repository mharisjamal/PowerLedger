# PowerLedger's data server

A Cloudflare Worker, `powerledger-data`, written in TypeScript. It stores the daily reports users choose to send,
in an R2 bucket, and indexes them in a D1 database for the owner's export and admin tools. See
`docs/superpowers/specs/2026-09-24-powerledger-data-sharing-design.md` §6 for the design.

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

## Running the tests

```
npm ci
npm test
```

`npm test` runs the Worker's own tests (`vitest`, against a local Miniflare D1 and R2 — no network, no Cloudflare
account needed) and the export tool's pure-function tests (`node --test`, plain Node).

## One-time setup, at deploy

These are the lead's commands (design §6, plan L5), run once when the Worker is first deployed:

```
npx wrangler login
npx wrangler d1 create powerledger-index          # then set the returned id in wrangler.toml
npx wrangler r2 bucket create powerledger-data
npx wrangler d1 migrations apply powerledger-index --remote
npx wrangler deploy
```

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
