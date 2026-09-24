import { sha256hex, timingSafeEqualStrings } from "../auth";
import { checkSession, finishSession, type MemberRow, type SessionRow, sessionToken } from "./auth";
import { base64urlDecode, hex, sha256 } from "./encoding";
import {
  addMemberStatement,
  currentEpoch,
  HOUSEHOLD_ID,
  isEnvelopeBody,
  isEpoch,
  MAX_MEMBERS,
  MAX_SEALED_LIST_CHARS,
  readSmall,
} from "./households";
import { errorResponse, ok, overAddressLimit, parseObject } from "./http";

/** At most this many PCs wait to join one household at a time. */
export const MAX_WAITING = 16;
/** At most this many PCs of one account wait at a time, so one account can't fill a household's list. */
export const MAX_WAITING_PER_ACCOUNT = 2;
/** A join request lasts this long, approved or not (the daily cron clears it). */
export const JOIN_REQUEST_LIFETIME_MS = 24 * 60 * 60 * 1000;
const MAX_RECOVERY_CHARS = MAX_SEALED_LIST_CHARS;
const VERIFIER_BYTES = 32;
/** An approval's commit, nonce and reveal (plan 0.9). */
const NONCE_BYTES = 32;

async function linkOf(env: Cloudflare.Env, account: string): Promise<string | null> {
  const row = await env.DB.prepare("SELECT household FROM account_households WHERE account = ?")
    .bind(account)
    .first<{ household: string }>();
  return row?.household ?? null;
}

async function isCurrentMember(env: Cloudflare.Env, household: string, device: string): Promise<boolean> {
  const row = await env.DB.prepare("SELECT 1 FROM members WHERE household = ? AND device = ? AND removed IS NULL")
    .bind(household, device)
    .first();
  return row !== null;
}

/** POST /v1/account/household: {"householdId"}, which this PC must be a current member of, links the account to that
 * household. An account links to one household; linking it again to the same one is done. */
export async function handleLink(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const household = parseObject(body)?.householdId;
  if (typeof household !== "string" || !HOUSEHOLD_ID.test(household)) {
    return errorResponse(400, "householdId must be 32 lower-case hex characters.");
  }
  if (!(await isCurrentMember(env, household, session.device))) return errorResponse(403, "This PC isn't a member of that household.");

  await env.DB.prepare("INSERT INTO account_households (account, household, linked) VALUES (?, ?, ?) ON CONFLICT DO NOTHING")
    .bind(session.account, household, Date.now())
    .run();
  const linked = await linkOf(env, session.account);
  return linked === household ? ok({ householdId: household }) : errorResponse(409, "This account is linked to another household.");
}

/** POST /v1/account/requests: this PC, signed in as an account linked to a household it isn't in, asks to join it. Asking
 * again starts a fresh request, its commit, nonce and reveal cleared. A member then runs the approval (plan 0.9: commit,
 * nonce, reveal, approve) or denies it (DELETE …/requests/{device}). At most 16 PCs wait for a household, and 2 for an
 * account. */
export async function handleAskToJoin(env: Cloudflare.Env, session: SessionRow): Promise<Response> {
  const household = await linkOf(env, session.account);
  if (!household) return errorResponse(409, "This account isn't linked to a household yet.");
  if (await isCurrentMember(env, household, session.device)) return errorResponse(409, "This PC is already in the household.");

  const [forHousehold, forAccount] = await env.DB.batch<{ n: number }>([
    env.DB.prepare("SELECT COUNT(*) AS n FROM join_requests WHERE household = ? AND device != ?").bind(household, session.device),
    env.DB.prepare("SELECT COUNT(*) AS n FROM join_requests WHERE account = ? AND device != ? AND created > ?")
      .bind(session.account, session.device, Date.now() - JOIN_REQUEST_LIFETIME_MS),
  ]);
  if (forHousehold.results[0].n >= MAX_WAITING) return errorResponse(409, "Too many PCs are already waiting to join this household.");
  if (forAccount.results[0].n >= MAX_WAITING_PER_ACCOUNT) {
    return errorResponse(409, `This account already has ${MAX_WAITING_PER_ACCOUNT} PCs waiting to join.`);
  }

  await env.DB.prepare(
    `INSERT INTO join_requests (household, device, account, sign_key, dh_key, created) VALUES (?, ?, ?, ?, ?, ?)
     ON CONFLICT (household, device) DO UPDATE SET
       account = excluded.account, sign_key = excluded.sign_key, dh_key = excluded.dh_key, created = excluded.created,
       approver = NULL, commitment = NULL, nonce = NULL, reveal = NULL, approved_epoch = NULL`,
  )
    .bind(household, session.device, session.account, session.sign_key, session.dh_key, Date.now())
    .run();
  return ok({ householdId: household });
}

/** A request still waiting: its account still linked to its household, not yet approved, and less than 24 hours old. */
const WAITING_REQUEST = `
  FROM join_requests r JOIN account_households l ON l.account = r.account AND l.household = r.household
  WHERE r.household = ? AND r.created > ? AND r.approved_epoch IS NULL`;

interface RequestRow {
  device: string;
  account: string;
  sign_key: string;
  dh_key: string;
  created: number;
  approver: string | null;
  commitment: string | null;
  nonce: string | null;
  reveal: string | null;
}

/** The request of `device` to join the member's household while it's waiting; null otherwise. */
async function waitingRequest(env: Cloudflare.Env, household: string, device: string): Promise<RequestRow | null> {
  return env.DB.prepare(
    `SELECT r.device, r.account, r.sign_key, r.dh_key, r.created, r.approver, r.commitment, r.nonce, r.reveal
     ${WAITING_REQUEST} AND r.device = ?`,
  )
    .bind(household, Date.now() - JOIN_REQUEST_LIFETIME_MS, device)
    .first<RequestRow>();
}

/**
 * GET /v1/households/{hid}/requests: the PCs waiting to join, for a member to approve: each with its keys, the (opaque)
 * account it's signed in as, and the approval so far, "approver" (the committing member's device), "commit", "nonce"
 * (the waiting PC's) and "reveal", each null until set.
 */
export async function handleListRequests(env: Cloudflare.Env, member: MemberRow): Promise<Response> {
  const rows = await env.DB.prepare(
    `SELECT r.device, r.account, r.sign_key, r.dh_key, r.created, r.approver, r.commitment, r.nonce, r.reveal
     ${WAITING_REQUEST} ORDER BY r.created, r.device`,
  )
    .bind(member.household, Date.now() - JOIN_REQUEST_LIFETIME_MS)
    .all<RequestRow>();
  return Response.json(
    rows.results.map((row) => ({
      device: row.device,
      account: row.account,
      sign: row.sign_key,
      dh: row.dh_key,
      created: row.created,
      approver: row.approver,
      commit: row.commitment,
      nonce: row.nonce,
      reveal: row.reveal,
    })),
  );
}

/** DELETE /v1/households/{hid}/requests/{device}: a member denies a waiting PC; 404 when none is waiting. */
export async function handleDenyRequest(env: Cloudflare.Env, member: MemberRow, device: string): Promise<Response> {
  const denied = await env.DB.prepare("DELETE FROM join_requests WHERE household = ? AND device = ? RETURNING device")
    .bind(member.household, device)
    .first();
  return denied ? ok() : errorResponse(404, "That PC isn't waiting to join this household.");
}

/** A posted commit, nonce or reveal: base64url of 32 bytes, kept as sent; null otherwise. */
function readNonce(value: unknown): string | null {
  return typeof value === "string" && base64urlDecode(value)?.byteLength === NONCE_BYTES ? value : null;
}

/**
 * POST /v1/households/{hid}/requests/{device}/commit: {"commit"}, a member's commitment to its nonce. The first member to
 * commit becomes the request's approver, the only one that may reveal and approve; any other commit is 409, though the
 * same approver posting the same commit again is done.
 */
export async function handleCommit(env: Cloudflare.Env, member: MemberRow, device: string, body: Uint8Array): Promise<Response> {
  const commit = readNonce(parseObject(body)?.commit);
  if (!commit) return errorResponse(400, "commit must be 32 bytes, as base64url.");
  const request = await waitingRequest(env, member.household, device);
  if (!request) return errorResponse(404, "That PC isn't waiting to join this household.");

  const set = await env.DB.prepare(
    `UPDATE join_requests SET approver = ?1, commitment = ?2
     WHERE household = ?3 AND device = ?4 AND approver IS NULL AND approved_epoch IS NULL RETURNING device`,
  )
    .bind(member.device, commit, member.household, device)
    .first();
  if (set || (request.approver === member.device && request.commitment === commit)) return ok();
  return errorResponse(409, "A member has already committed to approving this PC.");
}

/**
 * POST /v1/account/requests/nonce: {"nonce"}, the waiting PC's own nonce, once a member has committed. Written once: the
 * same nonce again is done, another is 409.
 */
export async function handleRequestNonce(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const nonce = readNonce(parseObject(body)?.nonce);
  if (!nonce) return errorResponse(400, "nonce must be 32 bytes, as base64url.");
  const household = await linkOf(env, session.account);
  const request = household ? await waitingRequest(env, household, session.device) : null;
  if (!request || request.account !== session.account) return errorResponse(404, "This PC has no request waiting.");
  if (request.commitment === null) return errorResponse(409, "No member has committed to approving this PC yet.");

  const set = await env.DB.prepare(
    `UPDATE join_requests SET nonce = ?1
     WHERE household = ?2 AND device = ?3 AND commitment IS NOT NULL AND nonce IS NULL AND approved_epoch IS NULL
     RETURNING device`,
  )
    .bind(nonce, household, session.device)
    .first();
  return set || request.nonce === nonce ? ok() : errorResponse(409, "This PC has already sent its nonce.");
}

/**
 * POST /v1/households/{hid}/requests/{device}/reveal: {"nonce"}, the approver revealing the nonce it committed to, once the
 * waiting PC has sent its own. Only the approver may (403); written once, the same reveal again being done.
 */
export async function handleReveal(env: Cloudflare.Env, member: MemberRow, device: string, body: Uint8Array): Promise<Response> {
  const reveal = readNonce(parseObject(body)?.nonce);
  if (!reveal) return errorResponse(400, "nonce must be 32 bytes, as base64url.");
  const request = await waitingRequest(env, member.household, device);
  if (!request) return errorResponse(404, "That PC isn't waiting to join this household.");
  if (request.approver !== member.device) return errorResponse(403, "Only the member that committed can reveal.");
  if (request.nonce === null) return errorResponse(409, "The waiting PC hasn't sent its nonce yet.");

  const set = await env.DB.prepare(
    `UPDATE join_requests SET reveal = ?1
     WHERE household = ?2 AND device = ?3 AND approver = ?4 AND nonce IS NOT NULL AND reveal IS NULL AND approved_epoch IS NULL
     RETURNING device`,
  )
    .bind(reveal, member.household, device, member.device)
    .first();
  return set || request.reveal === reveal ? ok() : errorResponse(409, "The approver has already revealed.");
}

/**
 * GET /v1/account/requests: this PC's own requests, as {"requests":[{"device","household","approver":{"device","sign",
 * "dh"}|null,"commit","reveal","approved":{"epoch"}|null,"expires"}]}, expires being unix ms. An approved request is
 * given here once: reading it ends it.
 */
export async function handleOwnRequests(env: Cloudflare.Env, session: SessionRow): Promise<Response> {
  const rows = await env.DB.prepare(
    `SELECT r.household, r.created, r.approver, r.commitment, r.reveal, r.approved_epoch, m.sign_key, m.dh_key
     FROM join_requests r LEFT JOIN members m ON m.household = r.household AND m.device = r.approver
     WHERE r.device = ? AND r.account = ? AND r.created > ? ORDER BY r.created`,
  )
    .bind(session.device, session.account, Date.now() - JOIN_REQUEST_LIFETIME_MS)
    .all<{
      household: string;
      created: number;
      approver: string | null;
      commitment: string | null;
      reveal: string | null;
      approved_epoch: number | null;
      sign_key: string | null;
      dh_key: string | null;
    }>();

  const approved = rows.results.filter((row) => row.approved_epoch !== null).map((row) => row.household);
  if (approved.length > 0) {
    await env.DB.prepare(
      `DELETE FROM join_requests WHERE device = ? AND approved_epoch IS NOT NULL AND household IN (SELECT value FROM json_each(?))`,
    )
      .bind(session.device, JSON.stringify(approved))
      .run();
  }

  return Response.json({
    requests: rows.results.map((row) => ({
      device: session.device,
      household: row.household,
      approver: row.approver && row.sign_key && row.dh_key ? { device: row.approver, sign: row.sign_key, dh: row.dh_key } : null,
      commit: row.commitment,
      reveal: row.reveal,
      approved: row.approved_epoch === null ? null : { epoch: row.approved_epoch },
      expires: row.created + JOIN_REQUEST_LIFETIME_MS,
    })),
  });
}

/**
 * POST /v1/households/{hid}/requests/{device}/approve: {"epoch","body"}, the household key at its current epoch sealed
 * for the waiting PC (with the member list, HouseholdCrypto.WrapFor, from the approving member). Only the member that
 * committed may approve (403), and only after its reveal (409). In one step the PC becomes a member, its envelope is kept
 * and its request is marked approved at that epoch, staying so until the PC reads it; a full household leaves all three
 * as they were. Any epoch but the current one is 409, and so is an envelope the PC already has at it: one is never
 * overwritten.
 */
export async function handleApprove(env: Cloudflare.Env, member: MemberRow, device: string, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  if (!isEpoch(posted?.epoch) || !isEnvelopeBody(posted?.body, MAX_SEALED_LIST_CHARS)) {
    return errorResponse(400, 'The body must be {"epoch","body"}: the key and member list sealed for that PC, 16384 characters at most.');
  }

  const request = await waitingRequest(env, member.household, device);
  if (!request) return errorResponse(404, "That PC isn't waiting to join this household.");
  if (request.approver !== member.device) return errorResponse(403, "Only the member that committed can approve.");
  if (request.reveal === null) return errorResponse(409, "The approval's reveal hasn't been made yet.");

  // An approver that hasn't fetched the current key would hand over one the newer batches can't be opened with.
  const epoch = await currentEpoch(env, member.household);
  if (posted.epoch !== epoch) return errorResponse(409, `The household's key is at epoch ${epoch}; approve with that one.`);
  const sealedAlready = await env.DB.prepare("SELECT 1 FROM key_envelopes WHERE household = ? AND epoch = ? AND device = ?")
    .bind(member.household, epoch, device)
    .first();
  if (sealedAlready) return errorResponse(409, "That PC already has a key at this epoch.");

  const now = Date.now();
  const alreadyIn = await isCurrentMember(env, member.household, device);
  const results = await env.DB.batch([
    ...(alreadyIn ? [] : [addMemberStatement(env, member.household, device, request.sign_key, request.dh_key, now)]),
    env.DB.prepare(
      `INSERT INTO key_envelopes (household, epoch, device, from_device, body, created)
       SELECT ?1, ?2, ?3, ?4, ?5, ?6
       WHERE EXISTS (SELECT 1 FROM members WHERE household = ?1 AND device = ?3 AND removed IS NULL)
       ON CONFLICT (household, epoch, device) DO NOTHING
       RETURNING device`,
    ).bind(member.household, epoch, device, member.device, posted.body, now),
    env.DB.prepare(
      `UPDATE join_requests SET approved_epoch = ?3 WHERE household = ?1 AND device = ?2
       AND EXISTS (SELECT 1 FROM members WHERE household = ?1 AND device = ?2 AND removed IS NULL)`,
    ).bind(member.household, device, epoch),
  ]);
  if (!alreadyIn && results[0].results.length === 0) return errorResponse(409, `This household already has ${MAX_MEMBERS} PCs.`);
  // Another member's approval sealed one first, between the look above and this: that one stays.
  if (results[alreadyIn ? 0 : 1].results.length === 0) return errorResponse(409, "That PC already has a key at this epoch.");
  return ok();
}

function isSealed(value: unknown, maxChars: number): value is string {
  if (typeof value !== "string" || value.length > maxChars) return false;
  const bytes = base64urlDecode(value);
  return bytes !== null && bytes.byteLength >= 28;
}

/** A posted verifier: base64url of exactly 32 bytes; null otherwise. */
function readVerifier(value: unknown): Uint8Array | null {
  const bytes = typeof value === "string" ? base64urlDecode(value) : null;
  return bytes && bytes.byteLength === VERIFIER_BYTES ? bytes : null;
}

/**
 * PUT /v1/account/recovery: {"body","verifier","epoch","replace"} from a current member of the linked household (plan
 * 0.9). The body is the key and member list sealed under the recovery code's key; the verifier is 32 bytes made from the
 * code, which recover must send back, and only its SHA-256 is kept. 409 unless both hold: epoch is the household's
 * current epoch, and the caller holds the recovery, or replace is true, which is a new code and makes the caller the
 * holder. So a PC putting an old code again can't overwrite a newer one.
 */
export async function handlePutRecovery(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  const verifier = readVerifier(posted?.verifier);
  const replace = posted?.replace ?? false;
  if (!isSealed(posted?.body, MAX_RECOVERY_CHARS)) return errorResponse(400, "body must be the sealed recovery, as base64url.");
  if (!verifier) return errorResponse(400, "verifier must be 32 bytes, as base64url.");
  if (!isEpoch(posted?.epoch)) return errorResponse(400, "epoch must be the epoch the body was sealed at.");
  if (typeof replace !== "boolean") return errorResponse(400, "replace must be true or false.");

  const household = await linkOf(env, session.account);
  if (!household) return errorResponse(409, "This account isn't linked to a household yet.");
  if (!(await isCurrentMember(env, household, session.device))) {
    return errorResponse(403, "Only a PC in the household can set how to recover it.");
  }
  const epoch = await currentEpoch(env, household);
  if (posted!.epoch !== epoch) return errorResponse(409, `The household's key is at epoch ${epoch}; seal the recovery at that one.`);

  const verifierHash = hex(await sha256(verifier));
  const now = Date.now();
  if (replace) {
    await env.DB.prepare(
      `INSERT INTO recovery (account, body, verifier_hash, epoch, holder, updated) VALUES (?, ?, ?, ?, ?, ?)
       ON CONFLICT (account) DO UPDATE SET
         body = excluded.body, verifier_hash = excluded.verifier_hash, epoch = excluded.epoch, holder = excluded.holder,
         updated = excluded.updated`,
    )
      .bind(session.account, posted!.body, verifierHash, epoch, session.device, now)
      .run();
    return ok();
  }

  // Without replace, only the holder's own recovery is renewed; a first one is a new code, so it needs replace too.
  const renewed = await env.DB.prepare(
    `UPDATE recovery SET body = ?1, verifier_hash = ?2, epoch = ?3, updated = ?4
     WHERE account = ?5 AND holder = ?6 RETURNING holder`,
  )
    .bind(posted!.body, verifierHash, epoch, now, session.account, session.device)
    .first();
  return renewed ? ok() : errorResponse(409, "This PC doesn't hold this account's recovery code; a new code must replace it.");
}

/** GET /v1/account/recovery: {"body","epoch","holder"} (the holder being the PC that holds the code), for any PC signed
 * in as the account; 404 when there's none. */
export async function handleGetRecovery(env: Cloudflare.Env, session: SessionRow): Promise<Response> {
  const row = await env.DB.prepare("SELECT body, epoch, holder FROM recovery WHERE account = ?")
    .bind(session.account)
    .first<{ body: string; epoch: number; holder: string }>();
  if (!row) return errorResponse(404, "This account has no recovery.");
  return Response.json({ body: row.body, epoch: row.epoch, holder: row.holder });
}

/**
 * POST /v1/account/recover: {"verifier"}, which SHA-256 must match what PUT kept, at whatever epoch the recovery was
 * sealed. On the account's authority, with no approval, the calling PC becomes the household's only current member:
 * every other is removed at the current epoch. The code is used up: the recovery is deleted, and the PC makes a new code
 * and rotates the key. Answers {"household","epoch"}, the epoch being the household's current one. Like every signed
 * request, it's taken once.
 */
export async function handleRecover(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const verifier = readVerifier(parseObject(body)?.verifier);
  if (!verifier) return errorResponse(400, "verifier must be 32 bytes, as base64url.");

  // Only a recovery whose holder is still a current member counts: a removed PC's code must never bring it back.
  const row = await env.DB.prepare(
    `SELECT r.verifier_hash, l.household, h.epoch AS current
     FROM recovery r JOIN account_households l ON l.account = r.account JOIN households h ON h.id = l.household
     JOIN members m ON m.household = l.household AND m.device = r.holder AND m.removed IS NULL
     WHERE r.account = ?`,
  )
    .bind(session.account)
    .first<{ verifier_hash: string; household: string; current: number }>();
  if (!row) return errorResponse(404, "This account has nothing to recover.");
  if (!timingSafeEqualStrings(hex(await sha256(verifier)), row.verifier_hash)) {
    return errorResponse(403, "That isn't this account's recovery verifier.");
  }

  const now = Date.now();
  const alreadyIn = await isCurrentMember(env, row.household, session.device);
  await env.DB.batch([
    env.DB.prepare(
      `UPDATE members SET removed = ?1, removed_epoch = ?2
       WHERE household = ?3 AND device != ?4 AND removed IS NULL`,
    ).bind(now, row.current, row.household, session.device),
    ...(alreadyIn ? [] : [addMemberStatement(env, row.household, session.device, session.sign_key, session.dh_key, now)]),
    // Every recovery of the household goes, not this account's alone: the others' holders were just removed.
    env.DB.prepare("DELETE FROM recovery WHERE account IN (SELECT account FROM account_households WHERE household = ?)")
      .bind(row.household),
    env.DB.prepare("DELETE FROM join_requests WHERE household = ? AND device = ?").bind(row.household, session.device),
  ]);
  return Response.json({ household: row.household, epoch: row.current });
}

/** POST /v1/auth/signout: ends this PC's session, signed by the PC it was given to. A session already ended is done. */
export async function handleSignout(request: Request, env: Cloudflare.Env): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;
  const token = sessionToken(request);
  if (!token) return errorResponse(401, "This request needs a session.");

  const tokenHash = await sha256hex(token);
  const exists = await env.DB.prepare("SELECT 1 FROM sessions WHERE token_hash = ?").bind(tokenHash).first();
  if (!exists) return ok();

  const check = await checkSession(request, env);
  if (check instanceof Response) return check;
  const body = await readSmall(request);
  if (body instanceof Response) return body;
  const session = await finishSession(request, env, check, body);
  if (session instanceof Response) return session;
  // The session goes, and with it this PC's own requests to join as that account.
  await env.DB.batch([
    env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?").bind(tokenHash),
    env.DB.prepare("DELETE FROM join_requests WHERE device = ? AND account = ?").bind(session.device, session.account),
  ]);
  return ok();
}

/** DELETE /v1/account: the account, its link, every session, its PCs' join requests and its recovery envelope (households
 * design §7). The household and its members carry on without sign-in. */
export async function handleDeleteAccount(env: Cloudflare.Env, session: SessionRow): Promise<Response> {
  await env.DB.batch([
    env.DB.prepare("DELETE FROM join_requests WHERE account = ?").bind(session.account),
    env.DB.prepare("DELETE FROM recovery WHERE account = ?").bind(session.account),
    env.DB.prepare("DELETE FROM account_households WHERE account = ?").bind(session.account),
    env.DB.prepare("DELETE FROM sessions WHERE account = ?").bind(session.account),
    env.DB.prepare("DELETE FROM accounts WHERE id = ?").bind(session.account),
  ]);
  return ok();
}
