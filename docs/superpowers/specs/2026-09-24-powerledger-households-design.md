# Households: several PCs, one ledger

2026-09-24. The owner wants people with more than one PC, a laptop and a desktop say, to see what all of them use
together. Their two ideas:
- **Sign in** on each PC and add the PCs to an account.
- **Without an account:** find the other PC on the same Wi-Fi, add it, and confirm on it.

They chose:
- **Sync:** it works both when the PCs are apart (a laptop at work or school) and directly when they share a network.
- **Other people's PCs:** family members' PCs can count toward the same home.
- **All of it:** pairing now, and sign-in as well.

A phone or web view is not wanted now. Built as two parts in one release, 0.7.0:
- **N1:** households, pairing and sync, with no account needed.
- **N2:** optional sign-in, on top of N1.

## 1. The household

- **What a household is.** A group of PCs that share a secret, the **household key**: 32 random bytes, used with
  AES-256-GCM. It has an **epoch** that goes up each time the key is replaced. A PC belongs to one household at a time.
  The household is created when a PC adds its first other PC.
- **Each PC's own keys.** Every PC has its own key pairs: ECDSA P-256 to sign and ECDH P-256 to agree keys. P-256
  because both .NET and Cloudflare's WebCrypto have it built in.
  - The private keys are kept DPAPI-protected for the service's account, as the sharing install key is.
  - The **device ID** is the first 16 bytes of SHA-256 of the signing public key, as hex.
- **What a member PC shares:**
  - its name, which the user can edit and which defaults to the Windows PC name;
  - laptop or desktop;
  - its **hour rows**, one per complete hour, from `samples_1h`:

    | Field | What it holds |
    |---|---|
    | the hour's start | UTC |
    | total energy | kWh |
    | each part's energy | processor, graphics, display, rest |
    | idle energy | with the display on, and with it off |
    | seconds | on, on battery, idle |
    | how it was known | measured, calibrated and estimated seconds |
    | cost | micro-units, with the currency of the tariff in force |
    | changed at | so a later correction of the same hour replaces it |

  Nothing finer than an hour, and nothing about the PC's hardware beyond its name and kind.
- **Rows kept on each PC.** Rows from other members are kept in a new table, `household_rows`. When a member leaves,
  its rows stay, marked as from a PC that has left, until the user removes them.

## 2. What the user sees

- **A new Household page in the rail.**
  - Today, this week and this month: the household's energy and cost, then one bar per PC.
  - Each PC's own total, and when it last synced ("synced 2 minutes ago", "last seen 3 days ago").
  - Every member sees every PC's detail.
  - Totals in different currencies are shown side by side, never converted.
- **Before a household exists,** the page explains the feature and offers **Add a PC**.
- **Add a PC** has two ways in:
  - **On this network:** the PCs found, each with its name and whether it's already in this household. Pick one.
  - **Somewhere else:** a one-time code to type on the other PC.
- **The PC being added** shows a prompt: "Join Desktop-7's household?", with its comparison code, 482 913, large. It has
  **Join** and **Don't join** buttons. A PC already in another household is told that joining leaves that one.
- **The adding PC** asks its own user at the same time: "Does Laptop-2 show 482 913?", with **Codes match** and
  **Cancel**. The key goes only after both users have said yes.
- **Managing the household:** rename this PC, remove another PC, leave the household. Removing and leaving ask first.
- **Settings → Household:** one tick, "Let my other PCs find this one on the network", on by default.
- **The Report** can include the household. Its PDF gains a page per member PC when the user asks for it.

## 3. Pairing on the same network

- **Being found.** While the tick is on, the service announces the PC with DNS-SD over multicast DNS, through
  Windows' own `DnsServiceRegister`. The service is `_powerledger._tcp.local`; the instance name is a random ID made
  once, not the PC's name.
  - The TXT record carries the protocol version, the PC's display name and a household tag: HMAC-SHA256 of the instance
    ID under the household key, cut to 8 bytes. A member can work the tag out, and so the list can say "already in your
    household". A stranger can't tell which PCs share a household, since the tag differs for each PC.
  - The **port** is the service's listener, chosen by Windows and given in the SRV record.
- **Listening.** The service listens on TCP on the LAN. The installer adds a Windows Firewall rule for the service's
  program: inbound TCP, **Private** networks only. On a Public network nothing is announced, and **Somewhere else** is
  the way.
- **Finding.** The App asks the service to browse (`DnsServiceBrowse`, then `DnsServiceResolve`) and lists what answers
  within a few seconds, refreshing while the list is open.
- **The exchange**, over one TCP connection:
  1. The adding PC sends a hello with a fresh ephemeral ECDH P-256 public key, its device public keys, and a
     **commitment**: a hash of a random nonce it keeps to itself for now.
  2. The joining PC answers with its own hello: an ephemeral key and its device keys.
  3. Both derive per-direction keys with HKDF-SHA256 over the shared secret and a hash of both hellos, and encrypt
     everything after this with AES-256-GCM.
  4. The adding PC reveals its nonce; the joining PC checks it against the commitment.
  5. Both derive a 6-digit **comparison code** from the shared secret, both hellos and the nonce, as Bluetooth's numeric
     comparison and ZRTP do. The commitment is what makes it hold: the joining PC answered before it knew the nonce, and
     the adding PC was bound to the nonce before it saw the answer. So neither side, and no PC in the middle, can steer
     the code. A PC in the middle can make both screens show the same code only by a one-in-a-million chance per try.
     It can abort a try against the joining PC unseen, but every try counts against the limits below: at most about 20
     per 10 minutes, about 1 in 50,000 per pairing.
  6. Both PCs show the code: the joining PC's prompt, and the adding PC's "Does Laptop-2 show 482 913?". Only after
     both users have said yes does the adding PC send:
     - the household ID;
     - the key and its epoch;
     - the member list, each member's ID, name, kind, public keys and epochs.
  7. The joining PC answers `joined`, with a proof signed by its device key. The adding PC records it, then confirms,
     and only then does the joining PC enter. A pairing cut off before that leaves nothing behind on either side.
- **Limits and time-outs.**
  - A PC takes at most one pairing at a time, and either side can cancel it.
  - A prompt not answered in 2 minutes is **Don't join**.
  - Failed pairings from one address (IPv6 by /64): 3 in 10 minutes pause that address for 10 minutes. From all
    addresses together: 20 in 10 minutes pause pairing on the network. Pairing by code isn't paused.
- **Nobody to ask.** Pairing needs a signed-in user on the joining PC to press **Join**. With no App running there, it
  is refused.

## 4. Pairing through the server (Somewhere else)

- **The code.** The adding PC makes a code of 16 base32 characters, shown as `K7QM-2XHD-9PW4-R8TA`. It carries 80 bits
  of randomness, lasts 10 minutes and works once.
- **What the server sees.** It sees only a meeting ID, the first 16 bytes of SHA-256 of the code.
- **The exchange.** Each side posts its ephemeral and device public keys with an HMAC keyed by a key derived from the
  code. Each checks the other's HMAC, which the server can't forge without the code.
- **The rest** is as on the network: the key and member list go encrypted to the joining PC, after its user presses
  **Join** on the same prompt, now without a comparison code. The code itself vouches for the adding PC.

## 5. Sync

- **On the same network.** Members that find each other on the network sync directly:
  - each proves its device key by signing a challenge;
  - they swap the hour rows the other lacks, by (device ID, hour start, changed at).
- **Through the server,** each member, every 15 minutes while it has new rows:
  1. Batches them.
  2. Gzips the batch.
  3. Encrypts it with the household key: AES-256-GCM, a random 96-bit nonce, and the associated data being household
     ID, device ID, epoch and sequence number.
  4. Signs the result with its device key.
  5. Posts it: `POST /v1/households/{hid}/batches`.

  It fetches the others' batches since its cursor with `GET /v1/households/{hid}/batches?after=`, checks each
  signature against the member list it holds, decrypts, and upserts.
- **What the server knows:**
  - the household's members, as device IDs and public keys;
  - batch sizes and times.

  It never knows the key or anything in a batch.
- **How long the server keeps batches:** 90 days.
- **Snapshots.** Each member posts all its own rows again, the last 13 months:
  - after a new key takes effect;
  - after a new member joins;
  - at least every 30 days.

  So a newcomer, or a PC away longer than 90 days, reads the whole year from the server with the current key alone.
- **Requests** to the household endpoints are signed by a member's device key over method, path, time and body hash.
  The server checks the key is a current member and refuses a request more than 5 minutes old.

## 6. Changing who is in

- **Removing a PC.** Any member can remove another. It then:
  - makes a new household key with the epoch + 1;
  - encrypts the key to each remaining member's ECDH key, with HKDF and AES-GCM;
  - posts the removal and the key envelopes, signed.

  The server drops the removed PC from the member list. Each member fetches its envelope. Old keys are kept locally to
  read older batches.
- **Leaving** is removing yourself, done the same way by the PC that leaves, when it is online.
- **What a removed PC keeps.** It keeps what it already had, and can read nothing new.
- **Who is in.** Two things decide it:
  - **The server's member list** says which PCs are in. The server takes a member only from a current member's signed
    request with the new PC's own proof, and a removal from any member.
  - **Introductions** say whose keys this PC trusts:
    - its own pairings and approvals;
    - the member list it was given on joining;
    - the sealed, signed lists of members it already holds as current.

  A PC counts as in only when both agree. So the server alone can't slip in keys of its own, and a removed PC's own
  claims count for nothing.
- **Removals someone tells this PC about** stop direct network sync with that PC at once. The server's list, which only
  current members can change, decides the rest.
- **Order.** The server records the household's epoch at each add and removal. A PC may seal the key for an epoch only
  if it was added before that epoch and not removed before it, and its keys were introduced here.
- **A PC nobody introduces.** One the server lists but no member has introduced within 3 days is taken out again.

**Who this protects against.**
- **Protected:**
  - the server on its own;
  - a removed PC on its own, once the server has taken its removal;
  - a PC in the middle on the network.
- **Not claimed:**
  - a PC acting against the household while it is still a member;
  - a removed PC working together with the server.

## 7. Sign-in (N2)

- **Providers.** Sign in with Microsoft or Google: OpenID Connect in the user's own browser, with a loopback redirect
  to `http://127.0.0.1:{port}/` and PKCE (RFC 8252), done by the App.
  - The Worker checks the ID token: its signature against the provider's JWKS, issuer, audience, expiry and nonce.
  - It then keeps only the provider and the subject ID. The e-mail address is shown in the App from the token and never
    stored on the server.
- **What an account does:** it links to one household. After that:
  - **A PC that signs in** joins by approval, and the two PCs check a code first, as Matrix's device verification does.
    The server passes every key along, so the code is what keeps it honest.
    1. The server lists the new PC as waiting.
    2. A member commits to a random nonce.
    3. The new PC sends its own nonce, once per request.
    4. The member reveals its nonce.
    5. Both PCs show the same 6-digit **approval code**, worked out from both PCs' keys and both nonces. The member's
       App asks "A PC signed in as you asks to join your household. Approve it?"; the new PC's App asks "Does your
       other PC show 482 913?"
    6. Only when the user approves does the member post the household key, encrypted to the new PC's ECDH key, along
       with the member list. The new PC enters only when its own user has also said the codes match.

    Because of the commitments, a server that swaps keys makes the codes differ, except by a one-in-a-million chance
    per request.
    - **The member's side.** It pins the new PC's keys and nonce as it first reads them, and shows its code before it
      reveals its own nonce. So every code the server could learn has already been on screen.
    - **Limits.** A member takes one approval at a time, and at most 5 a day. The new PC answers one per request, and
      asks again only when its user says so. A waiting request lapses after a day.
    - **Pace.** While an approval is under way, both PCs check every 10 seconds, and both prompts stay up for 10
      minutes, so the user sees the two codes side by side.

    The server never has the key, as with Signal's or WhatsApp's linked devices. An unanswered prompt comes back later;
    it never counts as a no.
  - **A recovery code.** Shown once when an account is first linked: 24 base32 characters, "Save this code; with it and
    your account you can get your household back if you lose every PC."
    - **What is kept with the account:** the household key and member list, encrypted with a key derived from the code
      (PBKDF2-SHA256). The PC that made the code keeps it current; it is the only PC that holds the code's key.
    - **Signing in on a new PC with the code** restores the household without approval, as its only PC:
      - the others are removed, and the App says so before going ahead;
      - the code is then used up, and the PC shows a new one.
    - **When the recovery is gone.** If the PC holding the code is removed, the recovery is deleted, and the App offers
      a new code.
- **Sessions.** The Worker gives each signed-in PC a session token: 32 random bytes, kept hashed on the server and
  DPAPI-protected on the PC. The PC keeps it until it signs out or the account is deleted.
- **Deleting an account** removes the account, its link, its sessions and its recovery envelope. The household and its
  members carry on without sign-in.
- **What the owner sets up:**
  - An app registration in the Azure portal: a public client whose redirect is `http://127.0.0.1`, added in the
    manifest's `replyUrlsWithType` as `InstalledClient`, since the portal's text box refuses an http loopback IP.
  - A Google OAuth client of the Desktop kind.

  Both client IDs, and Google's client secret, are built into the App. For a native app with PKCE they aren't secrets.

## 8. Server

New routes on the same Worker, with D1 tables:
- `households(id, created)` and `members(household, device, sign_key, dh_key, added, removed)`. Names and kinds travel
  inside the encrypted batches, each PC's own with every batch it sends, so the server never has them;
- `batches(household, seq, device, epoch, bytes, received)`, with the bodies in the same body store as reports (D1 until
  R2 is on);
- `key_envelopes(household, epoch, device, body)` and `meetings(id, body, expires)` for pairing by code;
- for N2: `accounts(id, provider, subject, created)`, `account_households`, `sessions(token_hash, account, device,
  created)` and `recovery(account, body)`.

The limits:
- a household holds at most 16 PCs;
- a batch is at most 1 MB;
- at most 200 requests a day per PC;
- meetings expire after 10 minutes.

The daily cron clears batches past 90 days and expired meetings.

## 9. Where it runs

- **The service** holds the household:
  - the keys, the member list and `household_rows`;
  - announcing, listening and syncing, whether or not anyone is signed in.
- **The App:**
  - draws the Household page;
  - asks the service to browse, add, remove or leave;
  - shows the join and approve prompts the service pushes over the pipe;
  - does N2's browser sign-in.
- **The pipe** gains requests for all of these, and pushed prompts. The prompts go only to an App in the console
  session, the one at the screen, not to every client.

## 10. Privacy

- **Its own feature.** Households are separate from 0.6.0's data sharing: a separate choice, end-to-end encrypted,
  seen only by the household's members.
- **What the server keeps:** membership and timing, and for N2 the provider and subject ID.
- **What the network sees.** A PC that can be found shows its display name on the local network. The tick turns that
  off.
- **The privacy policy** gains a Households section, and a Sign-in section for N2.

## 11. Tests

- **Crypto.** HKDF, AES-GCM and ECDSA against known vectors. The comparison code is the same on both sides and
  differs when a key or the nonce changes.
- **The pairing state machines,** with an in-memory transport. The cases:
  - accepted, refused and timed out;
  - a PC in the middle that tries hellos on the side it answers and still can't match the codes;
  - a wrong reveal;
  - the rate limit;
  - already in another household.
- **Two real services in one process,** pairing over loopback TCP with discovery faked, then syncing directly and
  through the Worker running in Miniflare.
- **Removing and rotating:** a removed PC can't decrypt a batch from after its removal.
- **The Worker:**
  - signed-request checks, including old and replayed requests;
  - membership;
  - meetings: once only, and expiry;
  - key envelopes;
  - N2's token check, against a test JWKS.
- **The App:** the Household page's figures, the prompts and the Add a PC list.
- **On real hardware,** the owner pairs their own desktop and laptop, on the same Wi-Fi and by code. Windows Sandbox
  runs one PC only, so this can't be tested there.

## Honest limits

- **Networks that stop PCs seeing each other** (guest Wi-Fi, "AP isolation", some routers and firewalls): discovery
  finds nothing there, and the code way is the answer.
- **Without sign-in,** losing every PC loses the household. Each PC's own history is still on it while it exists.
- **A removed PC** keeps what it had already synced.
- **A PC away more than 90 days** reads the others' history from their latest snapshots, posted at least every 30
  days; rows a member changed between its last snapshot and going offline for good are lost with it.
- **Recovery restores the key as of the recovery's last update.** If every PC is lost before the PC holding the code
  has updated it after a key change, rows posted under the newer key can't be read.
- **The server sees** how many PCs a household has and when they sync.
