# PowerLedger Plan N: households — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** a person or family with several PCs pairs them into a household and sees what all of them use on a new
Household page. Pairing works on the same network or with a code. Sync goes direct on the LAN or end-to-end encrypted
through the Worker. Optional sign-in with Microsoft or Google adds PCs by approval and recovers a lost household.
Built to `docs/superpowers/specs/2026-09-24-powerledger-households-design.md` ("the design"), released as 0.7.0.

**Architecture:**
- **The service** holds the household, whether or not anyone is signed in: device keys, the household key, members,
  `household_rows`, DNS-SD announcing, the LAN listener, LAN sync and relay sync.
- **The App** draws the Household page from `household_rows`, which it reads from the database, read-only as it does
  history. It runs the Add a PC and prompt windows over the pipe, and N2's browser sign-in.
- **The Worker** gains households, members, key envelopes, batches and meetings, and for N2 accounts, sessions,
  requests and recovery. The server never holds a household key or any row.
- **Crypto** is P-256 ECDSA and ECDH, HKDF-SHA256 and AES-256-GCM: built into .NET and into WebCrypto.

**Tech stack:**
- .NET 10: WPF and the Worker service, SQLite, xUnit, Shouldly.
- Win32 `dnsapi` DNS-SD (`DnsServiceRegister`, `DnsServiceBrowse`, `DnsServiceResolve`) through P/Invoke.
- The Worker: TypeScript on Cloudflare Workers, D1 with the body store from 0.6.0, vitest with the Workers pool.

---

## Ground rules for every agent

- **Where to work.** Only in your own worktree and branch, named in your section. Private `TEMP`/`TMP`:
  `mkdir -p "$TEMP/pl-n-<you>"` and prefix every `dotnet`/`npm` command with
  `TEMP="$TEMP/pl-n-<you>" TMP="$TEMP/pl-n-<you>"`.
- **Test-first.** Each step: the failing test, watch it fail, the code, watch it pass, commit.
- **.NET tests.** Filter `--filter "Category!=Hardware&Category!=UI&Category!=Installed"`. Never start, stop or
  reinstall the real PowerLedger service. Tests that need two services run both in-process on loopback, with discovery
  faked.
- **Line endings.** The working tree is CRLF. Edit existing files with the Edit tool. New `.cs`/`.xaml` files get CRLF
  (convert as bytes). JSON and TS may be LF.
- **Commits.**
  - Stage explicit paths.
  - One plain-English sentence per message, like the history, with no attribution.
  - Never push.
- **Stay in your lane.** Task 0 is done by the lead before you start: build on it, don't edit it. If it's wrong, stop
  and report.
- **Wording.** User-facing text is plain English, sentence case. Wording given here is used exactly.
- **Downloads.** npmjs.com and NuGet are allowed.
- **Secrets.** Never run wrangler login, deploy or secret, nor any `--remote` command.

## Task 0 — the contract (lead, before Wave 1)

The lead writes and commits these on `plan-n/base` (from `main`), with their tests green. The agents build on them.

### 0.1 `src/PowerLedger.Contracts/Households.cs`

```csharp
namespace PowerLedger.Contracts;

/// <summary>A PC in the household, as this PC knows it (households design §1).</summary>
public sealed record MemberStatus(
    string DeviceId, string Name, ChassisKind Kind, bool IsThisPc, DateTimeOffset? LastSyncedAt, bool Left);

/// <summary>Where the household stands, in ServiceStatus.Household.</summary>
/// <param name="HouseholdId">Null while this PC is in no household.</param>
/// <param name="DeviceId">This PC's device ID, made on first use.</param>
/// <param name="Name">This PC's name in the household.</param>
/// <param name="Discoverable">Whether other PCs on a Private network can find this one.</param>
/// <param name="Problem">Null while syncing goes well; otherwise the last problem in words the App can show.</param>
/// <param name="SignedIn">N2: true while this PC holds a session.</param>
/// <param name="PendingApprovals">N2: PCs signed in as this account waiting for a member to approve them.</param>
public sealed record HouseholdStatus(
    string? HouseholdId, string DeviceId, string Name, ChassisKind Kind, bool Discoverable,
    IReadOnlyList<MemberStatus> Members, string? Problem, bool SignedIn = false, int PendingApprovals = 0);

/// <summary>A PowerLedger PC found on this network.</summary>
public sealed record FoundPc(string InstanceId, string Name, bool InThisHousehold);

public enum NoticeKind
{
    /// <summary>This PC is being added: show Join / Don't join, with the comparison code when there is one.</summary>
    JoinPrompt = 0,
    /// <summary>A pairing this PC started moved on: its code, or its outcome in Text.</summary>
    PairingProgress = 1,
    /// <summary>N2: a PC signed in as this account asks to join; show Approve / Don't approve.</summary>
    ApprovePrompt = 2,
    /// <summary>Something the user should know, in Text (a PC was removed, this PC was removed, …).</summary>
    Info = 3,
}

/// <summary>Pushed by the service to the App in the console session (households design §9).</summary>
/// <param name="PromptId">For prompts: what AnswerPromptRequest names.</param>
/// <param name="ComparisonCode">"482 913" for a pairing on the network; null for a code pairing.</param>
public sealed record HouseholdNotice(
    NoticeKind Kind, string? PromptId, string Text, string? FromName, string? ComparisonCode, DateTimeOffset? ExpiresAt)
    : PipeMessage;
```

### 0.2 Pipe messages

These go in `src/PowerLedger.Contracts/PipeMessages.cs`, registered by the name shown after each:

```csharp
public sealed record BrowsePcsRequest(long Id) : PipeRequest(Id);                                       // "browsePcs"
public sealed record FoundPcsReply(long Id, IReadOnlyList<FoundPc> Pcs) : PipeMessage;                  // "foundPcs"
public sealed record AddPcRequest(long Id, string InstanceId) : PipeRequest(Id);                        // "addPc"
public sealed record StartCodePairingRequest(long Id) : PipeRequest(Id);                                // "startCodePairing"
public sealed record JoinByCodeRequest(long Id, string Code) : PipeRequest(Id);                         // "joinByCode"
public sealed record AnswerPromptRequest(long Id, string PromptId, bool Accept) : PipeRequest(Id);      // "answerPrompt"
public sealed record RemovePcRequest(long Id, string DeviceId) : PipeRequest(Id);                       // "removePc"
public sealed record LeaveHouseholdRequest(long Id) : PipeRequest(Id);                                  // "leaveHousehold"
public sealed record RenamePcRequest(long Id, string Name) : PipeRequest(Id);                           // "renamePc"
public sealed record SetDiscoverableRequest(long Id, bool On) : PipeRequest(Id);                        // "setDiscoverable"
public sealed record SignInRequest(long Id, string Provider, string IdToken, string Nonce,
    string? RecoveryCode = null) : PipeRequest(Id);                                                     // "signIn"
public sealed record SignOutRequest(long Id) : PipeRequest(Id);                                         // "signOut"
public sealed record DeleteAccountRequest(long Id) : PipeRequest(Id);                                   // "deleteAccount"
/// <summary>What a household request did, and for StartCodePairing the code to show.</summary>
public sealed record HouseholdReply(long Id, bool Ok, string Message, string? Code = null) : PipeMessage; // "household"
```

`HouseholdNotice` registers as `"householdNotice"`. `ServiceStatus` gains a trailing `HouseholdStatus? Household = null`.
Every request answers within 8 s. A pairing that takes longer reports through pushed `HouseholdNotice`s.

### 0.3 `src/PowerLedger.Core/Households/HouseholdCrypto.cs`

```csharp
using System.Security.Cryptography;
using System.Text;

namespace PowerLedger.Core.Households;

/// <summary>This PC's own keys: P-256 ECDSA to sign, P-256 ECDH to agree keys (households design §1).</summary>
public sealed class DeviceKeys : IDisposable
{
    public ECDsa Sign { get; }
    public ECDiffieHellman Dh { get; }
    public DeviceKeys(ECDsa sign, ECDiffieHellman dh) { Sign = sign; Dh = dh; }
    public static DeviceKeys Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
    public byte[] SignPublic => Sign.ExportSubjectPublicKeyInfo();
    public byte[] DhPublic => Dh.ExportSubjectPublicKeyInfo();
    public string DeviceId => HouseholdCrypto.DeviceIdOf(SignPublic);
    /// <summary>The two private keys as PKCS#8, each kept DPAPI-protected on its own.</summary>
    public (byte[] Sign, byte[] Dh) ExportPrivate() => (Sign.ExportPkcs8PrivateKey(), Dh.ExportPkcs8PrivateKey());
    public static DeviceKeys FromPrivate(byte[] sign, byte[] dh)
    {
        var signKey = ECDsa.Create();
        signKey.ImportPkcs8PrivateKey(sign, out _);
        var dhKey = ECDiffieHellman.Create();
        dhKey.ImportPkcs8PrivateKey(dh, out _);
        return new(signKey, dhKey);
    }
    public void Dispose() { Sign.Dispose(); Dh.Dispose(); }
}

public static class HouseholdCrypto
{
    /// <summary>First 16 bytes of SHA-256 of the signing key's SPKI, lower-case hex.</summary>
    public static string DeviceIdOf(byte[] signSpki) => Convert.ToHexStringLower(SHA256.HashData(signSpki)[..16]);

    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Encoding.UTF8.GetBytes(info));

    /// <summary>nonce (12) ‖ ciphertext ‖ tag (16), AES-256-GCM with a random nonce.</summary>
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        var sealedBytes = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(sealedBytes.AsSpan(0, 12));
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(sealedBytes.AsSpan(0, 12), plaintext, sealedBytes.AsSpan(12, plaintext.Length), sealedBytes.AsSpan(12 + plaintext.Length), aad);
        return sealedBytes;
    }

    /// <summary>Throws CryptographicException when the key, the associated data or the bytes are wrong.</summary>
    public static byte[] Open(byte[] key, byte[] sealedBytes, byte[] aad)
    {
        if (sealedBytes.Length < 28) throw new CryptographicException("Too short to be sealed.");
        var plaintext = new byte[sealedBytes.Length - 28];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(12, plaintext.Length), sealedBytes.AsSpan(12 + plaintext.Length), plaintext, aad);
        return plaintext;
    }

    /// <summary>ECDSA P-256 over SHA-256, IEEE P1363 (r ‖ s, 64 bytes) as WebCrypto signs.</summary>
    public static byte[] SignData(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(byte[] signSpki, byte[] data, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(signSpki, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static byte[] Agree(ECDiffieHellman mine, byte[] theirDhSpki)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(theirDhSpki, out _);
        return mine.DeriveRawSecretAgreement(theirs.PublicKey);
    }

    /// <summary>The 6-digit comparison code both screens show (households design §3), "482 913".</summary>
    public static string ComparisonCode(byte[] shared, byte[] ephAdder, byte[] ephJoiner)
    {
        var bytes = Hkdf(shared, [.. ephAdder, .. ephJoiner], "powerledger comparison code", 4);
        var number = (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) % 1_000_000).ToString("D6");
        return $"{number[..3]} {number[3..]}";
    }

    /// <summary>Wraps a key for one member: ECDH with its DH key, HKDF with the context, AES-GCM.</summary>
    public static byte[] WrapFor(ECDiffieHellman mine, byte[] theirDhSpki, byte[] key, string context) =>
        Seal(Hkdf(Agree(mine, theirDhSpki), [], "powerledger key wrap|" + context), key, Encoding.UTF8.GetBytes(context));

    public static byte[] UnwrapFrom(ECDiffieHellman mine, byte[] theirDhSpki, byte[] wrapped, string context) =>
        Open(Hkdf(Agree(mine, theirDhSpki), [], "powerledger key wrap|" + context), wrapped, Encoding.UTF8.GetBytes(context));

    /// <summary>What a signed request covers (households design §5): METHOD \n path \n unix seconds \n hex SHA-256 of the body.</summary>
    public static byte[] RequestToSign(string method, string pathAndQuery, long unixSeconds, byte[] body) =>
        Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}\n{pathAndQuery}\n{unixSeconds}\n{Convert.ToHexStringLower(SHA256.HashData(body))}");

    /// <summary>A batch's associated data: household ‖ device ‖ epoch ‖ sequence.</summary>
    public static byte[] BatchAad(string householdId, string deviceId, int epoch, long sequence) =>
        Encoding.UTF8.GetBytes($"{householdId}|{deviceId}|{epoch}|{sequence}");

    /// <summary>The DNS-SD household tag: HMAC-SHA256(instance ID) under the household key, 8 bytes, hex.</summary>
    public static string HouseholdTag(byte[] householdKey, string instanceId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(householdKey, Encoding.UTF8.GetBytes(instanceId))[..8]);
}
```

### 0.4 `src/PowerLedger.Core/Households/PairingCode.cs`

The code alphabet is Crockford base32 (`0123456789ABCDEFGHJKMNPQRSTVWXYZ`). Methods:
- `New()`: 10 random bytes, giving 16 characters, shown `XXXX-XXXX-XXXX-XXXX`.
- `Normalize(string)`: upper case, drop spaces and dashes, map `O`→`0` and `I`/`L`→`1`. Null when it isn't 16
  alphabet characters.
- `MeetingId(normalized)`: the first 16 bytes of SHA-256 of the ASCII, as hex.
- `Key(normalized)`: `Hkdf(ascii, [], "powerledger code key")`.
- `Mac(key, side, eph, sign, dh)`: HMAC-SHA256 over `side ‖ eph ‖ sign ‖ dh`.

`RecoveryCode.New()` for N2 is the same with 15 bytes, giving 24 characters. `RecoveryCode.Key(code)` is PBKDF2-SHA256
with 100,000 iterations, the salt `"powerledger recovery"` and 32 bytes; the code already carries 120 bits.

### 0.5 Tests and the vectors the Worker checks

- **Tests,** in `tests/PowerLedger.Core.Tests/HouseholdCryptoTests.cs` and `PairingCodeTests.cs`:
  - HKDF against RFC 5869 test case 1;
  - seal/open round trips, and a wrong key or associated data throws;
  - sign/verify, including a flipped bit;
  - `Agree` gives the same secret both ways;
  - the comparison code is equal on both sides and changes when one ephemeral key changes;
  - a wrap/unwrap round trip;
  - `Normalize` over dashes, lower case, O/0 and I/L/1;
  - `MeetingId` is stable;
  - the device ID is 32 hex characters.
- **Vectors.** A small console step writes `server/test/fixtures/households/vectors.json`: one device's `signSpki`
  (base64url), a signed request (method, path, time, body, signature) and a MAC for a meeting body. It is committed.
  The Worker's tests verify the signature and the MAC with WebCrypto, so the two sides can't drift.

### 0.6 The wire formats (for S, C and A)

- **Signed request headers:**
  - `X-PL-Device`: the device ID;
  - `X-PL-Time`: unix seconds;
  - `X-PL-Signature`: base64url of `SignData(RequestToSign(method, pathAndQuery, time, body))`.

  The Worker refuses a request whose time is more than 300 s off.
- **A batch as posted:**
  `{"device":"…","epoch":3,"seq":17,"body":"<base64url sealed>"}`.
  - `body` is `Seal(householdKey, gzip(json), BatchAad(hid, device, epoch, seq))`.
  - The plaintext json is `{"v":1,"device":{"id","name","kind"},"rows":[{…}]}`.
- **A row:**
  `{"hour":<unix ms>,"energyWh","cpuWh","gpuWh","displayWh","restWh","idleOnWh","idleOffWh","onS","batteryS","idleS","measuredS","calibratedS","estimatedS","costMicro","currency","changed":<unix ms>}`.
- **LAN frames:** a 4-byte big-endian length, then the frame, each at most 1 MB.
  - **Before the key exchange:** plaintext JSON
    `{"type":"hello","v":1,"purpose":"pair"|"sync","eph","sign","dh","name","kind","instance"}`, all keys base64url
    SPKI.
  - **After it:** each frame is AES-256-GCM with a counter nonce (4 zero bytes, then an 8-byte big-endian counter from
    0) under per-direction keys:
    - `Hkdf(shared, eph_adder ‖ eph_joiner, "powerledger lan a2j")`;
    - `…, "powerledger lan j2a")`.

    A frame out of order ends the connection.
  - **Pairing:**
    1. `{"type":"answer","accept":bool}` from the joiner;
    2. then `{"type":"welcome","household","epoch","key","members":[{"id","name","kind","sign","dh"}]}` from the adder;
    3. then `{"type":"joined"}`.
  - **Sync:**
    1. `{"type":"prove","sig"}`, the signature over `"sync" ‖ eph_a ‖ eph_b`, checked against the member list;
    2. then `{"type":"have","latest":{"<device>":<max changed ms>}}` both ways;
    3. then `{"type":"rows","rows":[…]}`;
    4. then `{"type":"done"}`.
- **Meeting slots:** `PUT /v1/meetings/{mid}/{slot}` and `GET …`, where the slot is `adder`, `joiner`, `answer` or
  `welcome`.
  - Each slot is written once, holds at most 8 KB and lives 10 minutes from the first `PUT`.
  - A `GET` of an empty slot gives 404.
  - `adder` and `joiner` hold the hello fields plus a `mac`.
  - `answer` and `welcome` are sealed with the session key.

### 0.7 Contract changes after the Worker's security review (they replace 0.6 where the two differ)

- **Joining proves its keys.**
  - `POST /v1/households/{hid}/members` takes `{"sign","dh","proof"}`, where `proof` is the joining PC's signature over
    `powerledger join|{hid}|{sign}|{dh}`.
  - The joiner sends it in its last LAN frame, `{"type":"joined","proof"}`. In a pairing by code it goes in a fifth
    meeting slot, `joined`, sealed like `answer` and `welcome`.
  - The same sign key with a different dh key gets 409.
- **The server keeps the household's epoch,** starting at 1.
  - `POST …/keys` must be the current epoch + 1.
  - An approval uses the current epoch and never overwrites an envelope.
- **Recovery (N2).**
  - `PUT /v1/account/recovery` takes `{"body","verifier"}`. The server keeps only SHA-256 of the verifier, and the
    household's current epoch.
  - `POST /v1/account/recover` takes `{"verifier"}` and works only at the current epoch, so a signed-in PC puts
    recovery again after every rotation.
- **Linking (N2)** requires `householdId`. A PC that finds its account unlinked links it again at the next sync.
- **Join requests (N2).**
  - They carry an opaque `account`, and sign-in returns the caller's own, so "signed in as you" is shown only when the
    two match.
  - `DELETE /v1/households/{hid}/requests/{device}` refuses a request.
  - At most 2 wait per account.
- **Statuses:**

  | Status | Means |
  |---|---|
  | 401 | not a member, or a bad signature: one message, so the two can't be told apart |
  | 410 | a valid signature from a removed member: this PC was removed |
  | 429 | past a daily cap: 1000 requests, 200 batches or 5 MB of batches per PC per UTC day |
  | 503 | the server's global caps are reached |

- **Removal** also:
  - deletes the recovery of every linked account;
  - unlinks the accounts that had a session on the removed PC;
  - deletes that PC's sessions for those accounts and its join requests.

### 0.8 Contract changes after the whole-branch review (they replace earlier sections where they differ)

- **Two-sided code check.**
  - After the key exchange, the adder pushes `ConfirmCode`: "Does {name} show {code}?", answered with Codes match or
    Cancel.
  - The welcome goes only after both this confirmation and the joiner's `answer:accept`. A cancel on either side sends
    `{"type":"cancel"}`, and the other side withdraws its prompt.
  - The code and both frame keys use `Transcript(adderHello, joinerHello)`, the hello frames' bytes, as HKDF salt.
- **Committing a join.** The joiner stages the welcome and commits only when the adder acknowledges `joined`, with
  `{"type":"welcomed"}` on the LAN or a sealed `welcomed` meeting slot. Otherwise it rolls back.
- **Signed batches.**
  - A batch posts `{"device","epoch","seq","body","sig"}`, where `sig` signs `BatchToSign(BatchAad(…), body)`. The
    Worker stores `sig` and returns it.
  - A receiver verifies it with the local member's sign key before opening the batch. It skips any device it doesn't
    know, and caps `changed` at now + 1 day.
- **Introducing members.** A new member is learned only:
  - from pairing;
  - from a proven LAN peer;
  - from a signed batch whose sealed payload carries `members:[{id,sign,dh,name,kind,added}]`.

  A batch's device key never introduces anyone.
- **Tombstones.** A removed member is kept with its time of removal and never re-added. `have` and the batch `members`
  list carry these removals.
- **Keys.**
  - Only a current member's envelope is taken.
  - A PC that leaves just removes itself; a staying member rotates the key when it sees the removal.
  - Rotation seals only to members the server lists as current.
  - A PC takes on the new epoch only once the server has taken the keys.
- **N2 approvals.**
  - The approver's `ApprovePrompt` carries `ApprovalCode(req.sign, req.dh, approver.dh)`.
  - The approved PC then pushes `ConfirmJoin` with the same code, worked out from the sealer's key, and joins only once
    its user confirms.
- **Recovery (N2).**
  - The code comes as a `RecoveryCode` notice, kept DPAPI-protected until it is answered.
  - `RecoveryMissing` in the status leads to `NewRecoveryCodeRequest`.
  - The recovery envelope's associated data includes the epoch.
  - Signing out clears this PC's recovery key.
- **Cancelling.** `CancelPairingRequest` stops a pairing, including a code meeting, and frees the pairing gate.
- **Withdrawing a prompt.** A `Withdraw` notice closes a prompt whose connection has gone.
- **Sessions.** Household changes are refused from a pipe client outside the console session.
- **Old rows.** `RemoveOldRowsRequest(Id, DeviceId?)` ("removeOldRows") deletes the rows of a PC that left or was
  removed, or with no device named every such PC's, which after leaving is all of them.
  - It is refused for this PC and for a current member.
  - The tombstone stays, so the PC is never added back.
  - The Household page lists a left PC only while its rows remain.

### 0.9 Contract changes after the security re-review (they replace earlier sections where they differ)

The re-review found:
- that a comparison code without a commitment can be steered by whoever sends second;
- that the approver's code could not be checked before it sealed;
- that removal and recovery leaned on wall clocks and on the server's word.

These changes answer all three. Core already has `NewNonce`, `Commitment`, `ComparisonCode(shared, transcript, adderNonce)`
and the six-part `ApprovalCode`. The Contracts already have `AskAgainRequest` ("askAgain") and `HouseholdStatus.CanAskAgain`.

**LAN pairing: commit, then reveal.**
- The adder's pair hello gains `"commit"`: base64url of `Commitment(nonce)`, for a fresh `NewNonce()`.
- After the hellos, the adder's first sealed frame is `{"type":"reveal","nonce"}`. The joiner checks it against
  `commit`; if it doesn't match, the pairing ends.
- The code is `ComparisonCode(shared, Transcript(adderHello, joinerHello), nonce)`. The two-argument form goes, and the
  frame keys don't change.
- The joiner asks its user only after a valid reveal. A pairing that ends before one counts as a failed attempt from
  that address.
- Limits on failed attempts:
  - per address: 3 in 10 minutes, with IPv6 counted by /64;
  - all addresses together: 20 in 10 minutes;
  - a full table never forgets a paused address; while it is full of paused ones, new addresses are refused;
  - failures on the network never pause pairing by code.

**Cancelling, and the household changing mid-pairing.**
- **The adder's step.** After `joined`, the adder records the member and queues the server add as one step, then sends
  `welcomed`.
  - A cancel before that step leaves nothing behind.
  - After it, the pairing completes, and the App is told the PC was added.
- **The joiner.** It doesn't cancel after sending `joined`; it rolls back only if `welcomed` never comes.
- **Checks and waits.**
  - Recording fails if the household ID is no longer the welcome's.
  - Cancelling frees the pairing gate only once the cancelled pairing has unwound.
  - Leaving, being removed, and joining by sign-in each wait for, or cancel, a pairing under way.
- **Code pairing.** While its prompt is up, the joiner watches the meeting, and withdraws the prompt when the adder
  cancels.

**Discovery, the pipe and names.**
- **Found PCs.**
  - At most 64 are kept, the most recently seen.
  - A PC not seen for 10 minutes is forgotten.
  - `FoundPcsReply` stays under the pipe's cap.
- **Oversized replies.** The pipe server answers a reply over 64 KB with an `ErrorReply`, never by dropping the
  connection.
- **Names.**
  - They are cleaned by Unicode scalar (`Rune`), dropping Cc, Cf, Cs, Co, Zl and Zp.
  - They are cut at 40 UTF-16 units, never between the two halves of a pair.
- **Private or not.** The network's category is checked every minute. The listener and the announcement stop within a
  minute of the network turning Public.

**Membership is ordered by epochs, not clocks.**
- **Fields.** Every member entry carries:
  - `addedEpoch`: the household epoch the adding PC was at;
  - once removed, `removedEpoch`: the epoch the removing PC was at;
  - wall-clock `added` and `removed`, for display only.
- **Merging.**
  - A member is current when it has no `removedEpoch`, or when `addedEpoch > removedEpoch`.
  - Merging two entries keeps the higher value of each field.
  - An epoch above this PC's own + 1 is ignored.
- **Removing** records `removedEpoch` = the remover's epoch, then the remover rotates the key, as now.
- **Adding back** a removed PC, by pairing or approval, needs the adder's epoch to be above its `removedEpoch`. The adder
  finishes any pending rotation first. The new `addedEpoch` is the adder's epoch. So a removed PC can come back by a new
  pairing, never by gossip.
- **Where membership is learned from:**
  1. this PC's own pairings, approvals and removals;
  2. the member list inside a welcome, an approval envelope or a recovery body (with its epochs and removals);
  3. the sealed member list in a batch, but only when its sender is current here;
  4. the server's list, for removals only. A PC it lists as removed gets
     `removedEpoch = max(its addedEpoch, this PC's current epoch)`, unless a higher one is already known.
- **Never learned:** anything from a removed sender's batch.
- **Rows from a relay batch** are taken only for the sender's own device. From a removed sender, they are taken only if
  the batch's epoch ≤ its `removedEpoch`.
- **A PC that knows no members yet** (just approved, or recovered) starts from the list in its envelope or body.

**Keys.**
- A sealer S may hand over the key for epoch N only when all of these hold:
  - S is current here;
  - S's `addedEpoch` < N;
  - S has no `removedEpoch`, or its `removedEpoch` ≥ N.

  There is no other allowance.
- **When a rotation (`POST …/keys`) fails:**
  - On 409 (that epoch is taken), it is given up. This PC takes that epoch's envelope if its sealer passes the rule
    above, and otherwise rotates to the next epoch.
  - On any other failure, the rotation stays queued, and nothing is posted to the relay under the old key meanwhile.
- **A pending rotation keeps its sealed envelopes,** so a retry posts the same bytes, and the Worker's identical-retry
  check then matches.

**Worker: members, batches and endings.**
- **Member rows** keep `added_epoch` and `removed_epoch`, the household's epoch at each change. `GET …/members` returns
  them along with the keys. Adding a removed PC back sets `added_epoch` and clears `removed_epoch`.
- **Batch numbers never repeat.** They come from `households.next_seq`, taken atomically. Retention deletes batches but
  never resets the counter.
- **When the last member goes:**
  - the household is kept as ended, with its members kept as removed;
  - its former members get 410, not 401;
  - removing a PC that is already removed answers 200.
- **Removal leaves accounts alone,** with two exceptions:
  - removing the PC that holds the recovery deletes the recovery;
  - when the household ends, every link and every recovery goes.

**Service queue.**
- Each queued op belongs to a household.
- Entering another household drops the old household's ops.
- A remove or leave that gets 410 counts as done.

**Catching up.**
- **Snapshots.** Each PC re-posts all its own rows to the relay:
  - after a rotation takes effect;
  - after a new member joins;
  - at least every 30 days.

  It does so at most once a day, unless the epoch changed. So a PC that was away, or is new, can read the whole
  history with the current key.
- **Change times.**
  - If this PC's newest change time is more than a day ahead of now, its rows are re-based to now, once.
  - After that, changes carry on from `max(now, newest + 1)`.

**N2 approvals: commit, then reveal, with both screens showing the code before anything is sealed.**
1. The waiting PC R posts its request, as now.
2. A member P that sees it:
   - picks `NewNonce()` and posts `{"commit"}` to `POST /v1/households/{hid}/requests/{device}/commit`;
   - runs at most one approval at a time, and at most 5 new ones a day.
3. R polls `GET /v1/account/requests`.
   - When it sees the approver `{device, sign, dh}` and the `commit`, it posts `{"nonce"}` once to
     `POST /v1/account/requests/nonce`.
   - R answers one commit per request. It makes a new request only when its user signs in or asks again
     (`AskAgainRequest`); it never asks again by itself.
4. P sees R's nonce, posts `{"nonce"}` to `POST …/requests/{device}/reveal`, and pushes its `ApprovePrompt` with
   `ApprovalCode(R.sign, R.dh, P.sign, P.dh, nonceR, nonceP)`.
5. R sees the reveal and checks it against the commit. It then pushes `ConfirmJoin` with the same code at once, before
   any approval, with text like "Does your other PC show 482 913? Approve it there too."
6. P's answer:
   - Approve seals the current key and the member list to R, as now, in one step.
   - Don't approve deletes the request.
   - An unanswered prompt closes and comes back at P's next turn while the request is still waiting. It never counts as
     a no.
7. R enters only when both hold:
   - its user has said the codes match;
   - the approval is there, sealed by the approver it committed with, for the epoch the server names.

   What else can happen on R:
   - "They don't match" deletes R's request, or makes R leave if it was already added.
   - An unanswered `ConfirmJoin` comes back at R's next turn.
   - A request that is refused or expires sets `CanAskAgain`.
- **Worker side:**
  - **Once each.** The commit, nonce and reveal are each written once per request. An identical retry by the same PC
    answers 200, and so does an identical approve.
  - **Who moves it on.** Only the PC that committed may reveal, and only after R's nonce. Only that PC may approve, and
    only after its reveal.
  - **Checks at the write.**
    - The approve's write checks the request is still as it read it.
    - It also checks the household's epoch hasn't changed.
    - A rotation's epoch claim needs an envelope for every current member at the moment of the claim.
  - **How long a request lasts.**
    - A waiting request expires after 24 hours.
    - An approved one stays until R deletes it, or for 7 days after the approval. Deny is refused once a request is
      approved.
  - **R's own request.** `DELETE /v1/account/requests` deletes it. R calls it when its user says the codes don't match,
    and once after entering.
  - **When the committing PC is removed,** the requests it committed to are deleted, and R may ask again.
- **Size.** The member list in an approval or recovery body carries:
  - the current members with their keys;
  - at most the 64 most recent removals, as ID and epochs only.

  Older removals come from the server's member list, which keeps every removed row. Bodies are at most 16384
  characters.

**N2 recovery: one holder, a code used once, and a verifier from the code.**
- **One holder.** Only the PC that made the code keeps the code's key, and it alone puts the recovery. No other PC holds
  it; signing in with a code does not keep it.
- **The verifier** is `Hkdf(codeKey, [], "powerledger recovery verifier")`. The Worker keeps its SHA-256, as now.
- **`PUT /v1/account/recovery`** takes `{"body","verifier","epoch","replace"}`. The Worker answers 409 unless both hold:
  - `epoch` is the household's current epoch;
  - the caller is the holder, or `replace` is true. `replace` is for a new code, and makes the caller the holder.

  A holder that gets 409 because a newer code exists forgets its key.
- **The body** carries the key and the member list, with its epochs.
- **`GET /v1/account/recovery`** returns `{body, epoch, holder}`.
- **Recovering** (`POST /v1/account/recover`, which checks the verifier):
  - needs the stored holder to be a current member;
  - works once, conditioned on that recovery still existing;
  - works at any stored epoch;
  - deletes every recovery of the household, and the requests of the PCs it removes;
  - makes the recovering PC the household's only current member, removing all others at the current epoch;
  - uses up the code: the recovery is deleted, and the recovering PC makes a new code, shows it (a `RecoveryCode`
    notice) and rotates the key.
- **Warnings and refusals.**
  - Before signing in with a recovery code, the App warns that the household's other PCs will be removed.
  - A recovery-code sign-in on a PC in another household is refused, like a sign-in whose account is linked elsewhere.
- **RecoveryMissing** means signed in, linked, and either no recovery on the server or a holder that isn't a current
  member.

**Sign-in redirect.** Back to `http://127.0.0.1:{port}/`, for both the listener's prefix and the redirect URI (RFC 8252
§7.3; a `localhost` prefix answers on every address). The owner registers `http://127.0.0.1` in the Azure
app's manifest (`replyUrlsWithType`, type `InstalledClient`).
---

## Wave 1 — three agents in parallel

### Agent S — the Worker (`server/`)

Worktree `D:\PowerLedger-n\server`, branch `plan-n/server`.

- [ ] **S1. Migration `0003_households.sql`:**
  - `households(id TEXT PK, created INTEGER)`;
  - `members(household, device, sign_key, dh_key, added, removed NULL, PK(household, device))`;
  - `batches(household, seq INTEGER, device, epoch, bytes, received, r2_key, PK(household, seq))`, with bodies through
    `store.ts`;
  - `key_envelopes(household, epoch, device, body, PK(household, epoch, device))`;
  - `meetings(id, slot, body, created, PK(id, slot))`;
  - `device_requests(device, utc_day, count, PK(device, utc_day))`.
- [ ] **S2. `src/households/auth.ts`.**
  - `verifySigned(request, env, body)` checks the three headers, the time window, and that `X-PL-Device` is a current
    member of the path's household (or, for create, that it matches the posted key). It verifies ECDSA P-256 over
    `RequestToSign`. On success it returns the member row; otherwise 401 or 403.
  - It counts 200 requests a day per device; past that, 429.
  - **Tests:** `vectors.json` verifies; a changed body, an old time and a non-member all fail.
- [ ] **S3. The household routes:**

  | Route | Does |
  |---|---|
  | `POST /v1/households` | create, `{"id","sign","dh"}`, signed by that key, the device ID derived from `sign` |
  | `POST /v1/households/{hid}/members` | add, `{"sign","dh"}`, signed by a member; at most 16 current members |
  | `DELETE /v1/households/{hid}/members/{device}` | a member removes, or leaves |
  | `POST /v1/households/{hid}/keys` | `{"epoch","envelopes":[{"device","body"}]}` |
  | `GET /v1/households/{hid}/keys/{epoch}` | returns the caller's own envelope |
  | `GET /v1/households/{hid}/members` | `[{"device","sign","dh","added","removed"}]` |

  **Tests:** each route and its refusals.
- [ ] **S4. Batches.**
  - `POST /v1/households/{hid}/batches`: at most 1 MB; the Worker assigns `seq` as max + 1; bodies go to `store.ts`.
  - `GET …/batches?after=<seq>&limit=100` gives `{"items":[{"seq","device","epoch","body"}],"next"}`, leaving out the
    caller's own.
  - A removed member gets 403.
  - **Tests** cover all of this.
- [ ] **S5. Meetings,** by the slot rules in 0.6, with no signature and the address rate limit.
  - **Tests:** write-once, 404 when empty, 413 over 8 KB, expiry.
- [ ] **S6. Retention:** batches past 90 days, meetings past 10 minutes, request counts past 2 days.
- [ ] **S7 (N2). `POST /v1/auth/signin`.**
  - The body is `{"provider":"microsoft"|"google","idToken","nonce","sign","dh"}`, signed by the device.
  - The ID token is checked RS256 against a cached JWKS (1 h):
    - **Microsoft:** `https://login.microsoftonline.com/common/discovery/v2.0/keys`, issuer
      `https://login.microsoftonline.com/{tid}/v2.0`;
    - **Google:** `https://www.googleapis.com/oauth2/v3/certs`, issuer `https://accounts.google.com`.
  - It also checks the audience against the env vars `MS_CLIENT_ID` and `GOOGLE_CLIENT_ID`, the expiry, and the nonce.
  - It upserts `accounts(id, provider, subject, created)` and gives back
    `{"session","householdId"|null,"hasRecovery"}`. The session is 32 random bytes, stored as `sessions(token_hash,
    account, device, created)`.
  - **Tests** use a test RSA key and a JWKS served from the test environment.
- [ ] **S8 (N2). Account routes,** all authenticated by `Authorization: Session <token>` plus the device signature:

  | Route | Does |
  |---|---|
  | `POST /v1/account/household` | link |
  | `POST /v1/account/requests` | a signed-in PC outside the household asks to join |
  | `GET /v1/households/{hid}/requests` | members see who's waiting |
  | `POST /v1/households/{hid}/requests/{device}/approve` | adds the member and stores its envelope in one step |
  | `PUT` and `GET /v1/account/recovery` | the recovery envelope |
  | `POST /v1/account/recover` | adds this device as a member on the account's authority, once it proves it holds the household key: an HMAC of the device ID under it |

  **Tests** cover each.
- [ ] **S9 (N2).** `POST /v1/auth/signout` deletes the session. `DELETE /v1/account` deletes the account, its links,
  sessions, requests and recovery. **Tests** cover both.

### Agent C — the service

Worktree `D:\PowerLedger-n\service`, branch `plan-n/service`.

- [ ] **C1. Storage V3:**
  - `household_rows(device_id, hour_ms, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, idle_on_wh, idle_off_wh,
    on_s, battery_s, idle_s, measured_s, calibrated_s, estimated_s, cost_micro INTEGER, currency, changed_ms,
    PRIMARY KEY(device_id, hour_ms))`;
  - `household_members(device_id PK, name, kind, sign_key BLOB, dh_key BLOB, added_ms, left_ms NULL,
    last_synced_ms NULL)`.

  **Tests:** migrating from 2 to 3 keeps the data; the upsert takes the newer `changed_ms`.
- [ ] **C2. `Households/HouseholdStore.cs`.**
  - It holds this PC's `DeviceKeys` (DPAPI, made once), the household ID, the key per epoch (DPAPI), the current epoch,
    `discoverable` (default true), the instance ID (made once), the relay cursor, the next batch sequence number, and
    N2's session (DPAPI).
  - **Tests:** round trips, and something that can't be decrypted means none.
- [ ] **C3. `Households/HourRows.cs`.**
  - Every hour, while in a household, it builds this PC's rows from `samples_1h`. Cost comes from
    `TariffRepository.Schedule()` at each hour, in micro-units and the tariff's currency.
  - It upserts them into `household_rows` under its own device ID, with `changed_ms` = now when the numbers changed.
  - On joining it backfills 13 months.
  - **Tests:** the figures match `samples_1h`; cost across a tariff change; changed only when different.
- [ ] **C4. `Households/Discovery.cs`.** `IDiscovery` has `Register(instance, port, txt)`, `Unregister()` and
  `BrowseAsync(timeout)`, the last giving `(instance, host, port, txt)`.
  - The Windows one uses `DnsServiceRegister`, `DnsServiceBrowse` and `DnsServiceResolve` from `dnsapi.dll`, with
    their callbacks.
  - It registers only while `discoverable` and the active network is Private, from
    `INetworkListManager.GetNetworkConnections` and its category; it re-checks when the network changes.
  - A `FakeDiscovery` is for tests. The real one is `[Trait("Category","Hardware")]`.
- [ ] **C5. `Households/Lan/Framing.cs` and `PairingSession.cs`.**
  - The framing and counter-nonce encryption are as in 0.6.
  - There are adder and joiner state machines over an `IFrameChannel`, with an in-memory pair for tests.
  - The joiner asks the App through `IPromptBroker`, which gives a `Task<bool>` with a 2-minute time-out.
  - The rules:
    - one pairing at a time;
    - 5 refusals in 10 minutes block pairing for 10 minutes;
    - a joiner already in a household is prompted with the leave warning.
  - **Tests:**
    - accept joins;
    - refuse and time-out change nothing;
    - a PC in the middle, which swaps the ephemeral keys, gives different codes and the pairing is refused;
    - out-of-order frames end the connection;
    - the rate limit.
- [ ] **C6. `Households/Lan/LanListener.cs` and `LanSync.cs`.**
  - A TCP listener on an OS-chosen port, used by C4's register.
  - Hellos with purpose `sync` go to `LanSync`: prove by signature, swap `have`, send the missing rows, upsert.
  - Each member found by browsing is synced every 15 minutes.
  - **Tests:** two services in one process on loopback exchange rows both ways; a non-member's prove is refused.
- [ ] **C7. `Households/Relay/RelayClient.cs` and `RelaySync.cs`.**
  - Signed requests to the Worker, at `SharingEndpoint.BuiltIn`, or the registry override made for sharing.
  - Every 15 minutes:
    - post the new rows as a batch;
    - fetch after the cursor, verify each batch's signer against the members, open it, upsert, and take names and
      kinds from each batch's `device`;
    - refresh the member list and fetch the key envelope of any new epoch.
  - Adding a member on the server after a LAN pairing is queued until it succeeds.
  - When a new member appears, each existing member posts the last 13 months of its own rows once, in batches of at most
    1 MB, so the newcomer sees the year.
  - **Tests** use the Worker in Miniflare where practical, else a fake `HttpMessageHandler`.
- [ ] **C8. `Households/CodePairing.cs`.**
  - The adder makes the code and PUTs `adder`, then polls `joiner`.
  - The joiner normalizes the code, PUTs `joiner` and GETs `adder`.
  - Each checks the MAC; they agree the session key; the joiner is prompted without a comparison code; `answer` and
    `welcome` go through the slots, sealed.
  - **Tests:** a good code joins; a wrong code fails on the MAC; expiry.
- [ ] **C9. `Households/HouseholdWorker.cs` and the pipe.**
  - A `BackgroundService` runs announcing, listening, hour rows and both syncs.
  - It handles the 0.2 requests, each answered within 8 s.
  - Removing:
    1. new key, epoch + 1;
    2. envelopes to each remaining member;
    3. `DELETE` the member;
    4. `POST` the keys.

    Leaving is the same, done for itself.
  - Pushes `HouseholdNotice`s only to pipe clients in the active console session. The client's session is
    `GetNamedPipeClientSessionId`; the console's is `WTSGetActiveConsoleSessionId`. With none there, a join prompt is
    refused at once.
  - `ServiceStatus.Household` carries the status.
  - **Tests** cover each request, the notice routing and the rotation.
- [ ] **C10. The installer.** `installer/PowerLedger.iss` adds, at install:
  `netsh advfirewall firewall add rule name="PowerLedger households" dir=in action=allow program="{app}\PowerLedger.Service.exe" protocol=TCP profile=private`.
  It deletes the rule by name at uninstall, and replaces it on an update. **Test:** `installer/test-installer.ps1`
  checks the rule after installing.
- [ ] **C11 (N2). Sign-in.**
  - `SignInRequest` posts `/v1/auth/signin` with the device's keys and stores the session.
  - If the account has a household and this PC isn't in it, it posts `/v1/account/requests` and says "Waiting for
    another PC in your household to approve this one." If it is in one and the account has none, it links.
  - On first link it makes a recovery code, PUTs the envelope, and gives the code back once in the `HouseholdReply`.
  - With `RecoveryCode` set, it recovers without approval.
  - **Tests** use the Miniflare Worker with the test JWKS.
- [ ] **C12 (N2). Approvals.**
  - Members poll `…/requests` with their sync and push an `ApprovePrompt`.
  - An approval posts the envelope and adds the member.
  - A key rotation also replaces the recovery envelope when this PC holds a session.
  - `SignOut` and `DeleteAccount` go to their routes.
  - **Tests** cover each.

### Agent A — the App

Worktree `D:\PowerLedger-n\app`, branch `plan-n/app`. Build against Task 0's records and the App's fakes, never the
service's classes.

- [ ] **A1. The Household page:** `Households/HouseholdViewModel.cs` and `HouseholdView.xaml`, with a rail entry
  "Household".
  - It reads `household_rows` and `household_members` through a new read-only `HouseholdQueries` in Storage.
  - It shows totals for today, this week and this month: energy and cost, grouped by currency; a bar per PC; each
    PC's name, kind and "synced 2 minutes ago" / "last seen 3 days ago"; and left PCs as "left".
  - With no household: the explanation and **Add a PC**.
  - **Tests:** the sums, the currency groups, the synced-ago wording, the empty state.
- [ ] **A2. Add a PC:** `Households/AddPcWindow.xaml` with two tabs.
  - **On this network:** `BrowsePcsRequest` every 5 s while open; each PC with "in this household" marked; **Add**
    sends `AddPcRequest`, and the window shows the comparison code from the progress notice ("On Laptop-2, check the
    code is 482 913 and press Join").
  - **Somewhere else:** `StartCodePairingRequest` shows the code, large and copyable, and a 10-minute countdown.
  - **Join a household with a code** is a text box and **Join**, sending `JoinByCodeRequest`.
  - **Tests:** the flow against the fake link's notices.
- [ ] **A3. Prompts.**
  - A `JoinPrompt` notice opens a modal on top: "Join {FromName}'s household?", the comparison code line when there is
    one, and the leave warning when this PC is in another household.
  - **Join** and **Don't join** send `AnswerPromptRequest`; a time-out closes it.
  - `Info` notices show as a tray notification.
  - **Tests** cover the prompt model.
- [ ] **A4. Managing.**
  - The Household page offers **Rename this PC** (`RenamePcRequest`, 1–40 characters), **Remove** on another PC and
    **Leave household**, each asking first.
  - Settings → Household has the tick "Let my other PCs find this one on the network" (`SetDiscoverableRequest`).
  - **Tests** cover each.
- [ ] **A5. The report.** The Report gains an "Include my household" tick; the PDF adds a household total and a page
  per member PC from `household_rows`. **Tests** cover the report model.
- [ ] **A6 (N2). Sign-in:** `Households/SignIn.cs`.
  - An OIDC authorization-code flow with PKCE through the system browser and a loopback listener on
    `http://127.0.0.1:{free port}/`. Microsoft uses `https://login.microsoftonline.com/common/oauth2/v2.0/authorize`;
    Google uses `https://accounts.google.com/o/oauth2/v2/auth`. Scopes are `openid email`.
  - It exchanges the code at the token endpoint and sends the ID token and nonce in a `SignInRequest`.
  - The client IDs are in `Households/SignInClients.cs`, filled in by the lead at L6. They are public for native clients.
  - The Household page gets **Sign in with Microsoft**, **Sign in with Google** and, when signed in, the e-mail from the
    ID token (kept in `ui.json` only), with **Sign out** and **Delete account**.
  - **Tests** cover PKCE, the state and the redirect parsing, against a fake browser.
- [ ] **A7 (N2). Recovery and approvals.**
  - When a `HouseholdReply` carries a recovery code, a window shows it once, with **Copy** and **Save as text file**, and
    "Keep this code safe. With it and your account you can get your household back if you lose every PC."
  - **Use a recovery code** is on the sign-in flow.
  - An `ApprovePrompt` notice opens "A PC signed in as you asks to join your household. Approve it?"
  - **Tests** cover each.

---

## Wave 2 — the lead

- [ ] **L1. Merge** S, C and A into `plan-n/base`. Build, and run every .NET test and `npm test`.
- [ ] **L2. Integration:** `tests/PowerLedger.Service.Tests/HouseholdEndToEndTests.cs`, with two service instances in
  one process and the Worker in Miniflare where it can be started, else the fake relay. It checks:
  - pairing on loopback with the prompt auto-answered;
  - rows flowing both ways, directly and through the relay;
  - removal rotating the key;
  - a code pairing.
- [ ] **L3. Documents:**
  - `PRIVACY.md` gains Households and Sign-in sections: what syncs, end-to-end encryption, what the server keeps (IDs,
    public keys, times; for N2 the provider and subject), 90 days for batches, and account deletion.
  - The README and the main design (§1, §8, §9, §11) are updated.
- [ ] **L4. Review:** a whole-branch review, then focused reviews on the fixes, until no finding is left open. Security
  first: the crypto use, signature checks, replay, pairing against a PC in the middle, and the key rotation.
- [ ] **L5. Sandbox:** the regression run on one PC, the existing 92 checks plus the Household page empty state, and
  that the firewall rule exists.
- [ ] **L6. The owner's setup and the real test.**
  - The owner creates an Azure app registration: personal and work accounts, a public client with a `http://localhost`
    redirect. They also create a Google OAuth client of the Desktop kind.
  - The lead puts the client IDs in `SignInClients.cs` and in the Worker's vars, applies migration 0003 remotely and
    deploys.
  - The owner then pairs their desktop and laptop on Wi-Fi and by code, leaves the laptop away a day to check relay
    sync, and signs in on both.
- [ ] **L7. Release 0.7.0:** the version, installers, CI, and `release.ps1` with notes that lead with households.
- [ ] **L8. Results:** fill in the section below, and the memory.

## Results

Filled in after the release.
