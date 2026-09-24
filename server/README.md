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
- `POST /v1/feedback` takes what a user writes in the App's feedback box and files it as an issue in the owner's
  PRIVATE GitHub repo (see Feedback, below).
- A daily cron (`retention.ts`) drops reports whose day is more than 3 years old, request counts more than 2 days
  old, and feedback's per-address counts for the hours that have passed.

## Households

`src/households/` holds the households routes (`docs/superpowers/specs/2026-09-24-powerledger-households-design.md`
§5 to §8). The server keeps membership, public keys and sealed batches; never a household key, a PC's name or a row.

- **Signed requests.** Every household and account request carries `X-PL-Device`, `X-PL-Time` (unix seconds, within
  300 s) and `X-PL-Signature`: base64url ECDSA P-256 (r ‖ s) over `METHOD\npath?query\ntime\nhex SHA-256(body)`, the
  path and query as sent. The headers and the signer (member row or session) are checked before the body is read.
  A signature is taken once, and a replay doesn't count against the PC's 1000 requests a UTC day. On household
  routes, no member row and a bad signature get the same 401; a removed PC gets 410 ("This PC was removed from the
  household."), and only once its signature is good. Keys are taken only in canonical SPKI (DER, point uncompressed).
- **Households.** `POST /v1/households` (`{"id","sign","dh"}`, signed by that key); `POST …/{hid}/members`
  (`{"sign","dh","proof"}`, where the proof is the joining PC's ECDSA P-256 signature, P1363, over UTF-8
  `powerledger join|{hid}|{sign}|{dh}` with the keys as posted: 400 without one, 403 when it doesn't verify; a current
  member with the same keys is 200, with another dh key 409); `DELETE …/{hid}/members/{device}` (200 for a PC already
  removed, 404 for one never a member). At most 16 members.
- **Members** are ordered by epochs, not clocks. `GET …/{hid}/members` gives
  `{"members":[{"device","sign","dh","added","removed","addedEpoch","removedEpoch"}]}`: the household's epoch when each
  was added and removed (`removed` and `removedEpoch` null while current), the times in unix ms for display. Adding a
  removed PC back sets `addedEpoch` again and clears its removal.
- **Keys.** The server keeps each household's epoch, 1 at creation. `POST …/{hid}/keys`
  (`{"epoch","envelopes":[{"device","body"}]}`, each envelope 1024 characters at most) takes only the current
  epoch + 1, sealed to exactly the current members (one for a PC that isn't current is 400; a current one left out is
  409). The envelopes and the epoch's move are one step that holds only while the members and the epoch are as they
  were, so a PC added or removed meanwhile makes it 409. Every 409 carries `{"epoch"}`, the household's epoch as it is
  then, which a PC whose epoch was taken rotates on from. A re-post of the very bytes the rotation to the current epoch
  was taken with, by the PC that sealed them, is 200, whatever changed since. `GET …/{hid}/keys/{epoch}` gives
  `{"epoch","from","body"}`, the caller's own.
- **Removing a PC**, or its leaving, leaves accounts alone, sessions and links included, except that a recovery the
  removed PC holds is deleted; it also clears the PC's request to join, and the waiting requests it had committed to
  approve, whose PCs may then ask again. So a PC approved but never entered can leave like any other, then ask again
  with the same session; its envelope at the epoch it left at is never sealed again, so its new approval comes after
  the rotation. **When the last member goes**, the household and its members stay, all removed, so a former member
  gets 410, not 401; its batches, key envelopes and requests, and every account's link to it, recovery for it and
  recover of it remembered for a retry, go.
- **Batches.** `POST …/{hid}/batches` (`{"device","epoch","seq","body","sig"}`, at most 1 MB, `seq` the sender's own,
  `sig` its 64-byte P1363 signature over `BatchToSign(BatchAad(…), body)`, required but not checked here: members check
  it); a PC posts at most 200 batches and 5 MB of them a UTC day (429), and the server takes at most 2 GB a day in all
  (503, "The server is busy; try again later."). The Worker numbers a household's batches from its own counter
  (`households.next_seq`), so a number never comes round again, even after retention. `GET …/{hid}/batches?after=&limit=` gives
  `{"items":[{"seq","device","epoch","body","sig"}],"next","more"}`: the others' batches as posted, `next` the cursor
  to send as `after` next time, `more` when another page waits.
- **Meetings**, for pairing by code: `PUT`/`GET /v1/meetings/{id}/{adder|joiner|answer|welcome|joined|welcomed}`, unsigned;
  each slot written once, 8 KB at most, for 10 minutes from the meeting's first `PUT`. Every `PUT` clears ended
  meetings; starting one has its own per-address limit (`MEETING_LIMIT`, 10 a minute, 429), and at most 5000 are live
  (503).
- **Sign-in.** `POST /v1/auth/signin` (`{"provider","idToken","nonce","sign","dh"}`) checks the ID token (RS256 only;
  issuer, a Microsoft key's own issuer, audience with azp for several, expiry, issue time, not-before) and gives
  `{"session","account","householdId","hasRecovery"}`, `account` being the account's opaque ID. The posted `nonce` is
  a salt: the token's nonce claim must be base64url(SHA-256(UTF-8(`<device ID>:<salt>`))) for the PC that signed the
  request, so a token only signs in the PC that asked for it. A Microsoft account is its tenant and subject.
- **Accounts**, with `Authorization: Session <token>` as well as the signature: `POST /v1/account/household`
  (`{"householdId"}`, required, a household the PC is a current member of); `POST /v1/auth/signout` (the session and
  the PC's own requests); `DELETE /v1/account` (its link, sessions, requests, recovery and recovers remembered).
- **Approvals** run commit, then reveal, so both screens show the code before anything is sealed; each step is
  written once (an identical retry by the same PC is 200, anything else 409), and a request lasts 24 hours.
  1. The waiting PC asks: `POST /v1/account/requests` (16 waiting a household, 2 an account; asking again starts a
     fresh request).
  2. A member commits: `POST …/{hid}/requests/{device}/commit` (`{"commit"}`, 32 bytes base64url), becoming the
     request's approver.
  3. The waiting PC, seeing it in `GET /v1/account/requests`, sends `POST /v1/account/requests/nonce` (`{"nonce"}`).
  4. The approver reveals: `POST …/{hid}/requests/{device}/reveal` (`{"nonce"}`, only the approver, 403 otherwise).
  5. The approver approves: `POST …/{hid}/requests/{device}/approve` (`{"epoch","body"}`, only after the reveal, the
     current epoch only, the sealed key and member list 16384 characters at most; an envelope the PC already has there
     is never overwritten, 409). It holds only if the request and the key are as the approval read them: a rotation, a
     denial or an ask-again in between gives 409. An identical retry of an approval made is 200, any other 409.
  6. The request stays, approved at that epoch, until the waiting PC withdraws it once it has entered
     (`DELETE /v1/account/requests`, which also withdraws a waiting request, as when its user says the codes don't
     match) or 7 days after the approval. A member can't deny it once approved (409).

  Members see `GET …/{hid}/requests`: `[{"device","account","sign","dh","created","approver","commit","nonce",
  "reveal"}]`, waiting ones only, the last four null until set, and deny with `DELETE …/{hid}/requests/{device}`. The
  waiting PC sees `GET /v1/account/requests`: `{"requests":[{"device","household","approver":{"device","sign","dh"}|null,
  "commit","reveal","approved":{"epoch"}|null,"expires"}]}`.
- **Recovery** has one holder, the PC that made the code. `PUT /v1/account/recovery`
  (`{"body","verifier","epoch","replace"}`: the sealed key and member list, 16384 characters at most, and a 32-byte
  verifier of which only SHA-256 is kept) is 409 unless `epoch` is the household's current epoch and the caller is the
  holder, or `replace` is true, which is a new code and makes the caller the holder; it's kept only while the caller is
  still a current member (403 otherwise) and the key still at that epoch. `GET` gives `{"body","epoch","holder"}`, or
  404. `POST /v1/account/recover` (`{"verifier"}`, 403 when it isn't the one put) works at any stored epoch, but only
  through a recovery whose holder is still a current member (404 otherwise). The caller becomes the household's only
  current member, every other removed at the current epoch with the requests they made or committed to, and every
  recovery of the household is deleted, all in one step that holds only while the code is unused: of two recovers
  racing, the second gets 404. It answers `{"household","epoch"}`, and for 10 minutes answers a retry by the same PC
  with the same verifier the same, though the code is used up (its first answer may have been lost); any other PC gets
  404.
- **Address limit.** Every household, account, sign-in and meeting route, and the reports ones, are behind
  `ADDRESS_LIMIT` (60 a minute), an IPv6 address counted by its /64.
- **Retention.** The daily cron also drops batches past 90 days, ended meetings, join requests waiting past 24 hours or
  approved more than 7 days ago, recovers remembered past their 10 minutes, per-PC counts and daily totals past 2 days
  and seen signatures past 10 minutes. Backlogs are worked through for up to 20 s a run, and each part runs on its own,
  so one failing doesn't stop the others.

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

## Feedback

`POST /v1/feedback`, JSON, unsigned, 8 MB at most (413):
`{"text","email","app","os","arch","log","images":[{"name","contentType","data"}]}`. `text` is required, 1 to 10000
characters; `email` (254 at most, something@something) and `log` (200 KB at most) may be null or left out; `app` is
the version (`0.7.0+sha`, 64 at most); `os` up to 100 characters; `arch` is `x64`, `arm64` or `x86`; `images` up to 5,
each a PNG (`image/png`, a name ending `.png`) or JPEG (`image/jpeg`, `.jpg` or `.jpeg`) as standard base64, 1 MB
decoded at most, its name up to 80 of `A-Z a-z 0-9 . _ -`, no two the same. Control and bidi characters are stripped
from every field before use. A bad field is 400 `{"error"}` with what's wrong; an address gets 10 an hour (an IPv6
one by its /64), then 429; and 503 (`{"error"}`) when feedback isn't set up on the server or GitHub fails, which the
App takes as "try again later".

Each piece gets an id, `yyyyMMdd-HHmmss-xxxx` (UTC). Its images, and its log when over 60k characters, are committed
to `feedback/<id>/<name>` on the repo's default branch, one commit each; then an issue is opened, labelled
`feedback`, titled with the text's first 60 characters and the app, os and arch, its body a table of the metadata
(id, app, os, arch, email or "not given", time UTC), the text in a fenced block, the images, and the log in a
`<details>` block or a link to its file. User text never lands in markdown as itself: the table's cells are inline
code, and the fences are longer than any run of backticks in the text. The answer is 202 `{"id","issue"}`. A GitHub
failure after some files were committed leaves them; the App's retry gets a new id.

To set it up (once):

1. Make a PRIVATE repo for it, say `PowerLedger-feedback`, and set `FEEDBACK_REPO = "<owner>/<repo>"` in
   `wrangler.toml`.
2. Make a fine-grained personal access token (GitHub → Settings → Developer settings → Fine-grained tokens) for that
   one repo, with **Contents: read and write** and **Issues: read and write**, nothing else.
3. Run `tools/set-feedback-token.ps1` yourself in `server\`. It asks for the token without showing it and sets it as
   the Worker secret `FEEDBACK_GITHUB_TOKEN`; nothing keeps it.
4. `npx wrangler d1 migrations apply powerledger-index --remote` (0005 adds the per-address counts) and
   `npx wrangler deploy`.

Until both are set, the route answers 503 "Feedback isn't set up on the server yet."

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
`set-admin-token.ps1`). `FEEDBACK_GITHUB_TOKEN` lives only as a Worker secret (`set-feedback-token.ps1` asks for it
and keeps nothing).
