import { sha256hex, timingSafeEqualStrings } from "../auth";
import { checkSession, finishSession, type MemberRow, type SessionRow, sessionToken } from "./auth";
import { base64urlDecode, hex, sha256 } from "./encoding";
import { addMemberStatement, currentEpoch, HOUSEHOLD_ID, isEnvelopeBody, isEpoch, MAX_MEMBERS, readSmall } from "./households";
import { errorResponse, ok, overAddressLimit, parseObject } from "./http";

/** At most this many PCs wait to join one household at a time. */
export const MAX_WAITING = 16;
/** At most this many PCs of one account wait at a time, so one account can't fill a household's list. */
export const MAX_WAITING_PER_ACCOUNT = 2;
/** A join request nobody has approved lapses after this (the daily cron clears it). */
export const JOIN_REQUEST_LIFETIME_MS = 7 * 24 * 60 * 60 * 1000;
const MAX_RECOVERY_CHARS = 4096;
const VERIFIER_BYTES = 32;

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

/** POST /v1/account/requests: this PC, signed in as an account linked to a household it isn't in, asks to join it. A
 * member approves it (POST …/requests/{device}/approve) or denies it (DELETE …/requests/{device}). At most 16 PCs wait
 * for a household, and 2 for an account. */
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
       account = excluded.account, sign_key = excluded.sign_key, dh_key = excluded.dh_key, created = excluded.created`,
  )
    .bind(household, session.device, session.account, session.sign_key, session.dh_key, Date.now())
    .run();
  return ok({ householdId: household });
}

/** The waiting request of `device` to join `household`, while its account is still linked to it and it hasn't lapsed. */
const WAITING_REQUEST = `
  FROM join_requests r JOIN account_households l ON l.account = r.account AND l.household = r.household
  WHERE r.household = ? AND r.created > ?`;

/** GET /v1/households/{hid}/requests: the PCs waiting to join, for a member to approve, with their keys and the
 * (opaque) account each is signed in as, which a PC can compare with its own from sign-in. */
export async function handleListRequests(env: Cloudflare.Env, member: MemberRow): Promise<Response> {
  const rows = await env.DB.prepare(
    `SELECT r.device, r.account, r.sign_key, r.dh_key, r.created ${WAITING_REQUEST} ORDER BY r.created, r.device`,
  )
    .bind(member.household, Date.now() - JOIN_REQUEST_LIFETIME_MS)
    .all<{ device: string; account: string; sign_key: string; dh_key: string; created: number }>();
  return Response.json(
    rows.results.map((row) => ({
      device: row.device,
      account: row.account,
      sign: row.sign_key,
      dh: row.dh_key,
      created: row.created,
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

/**
 * POST /v1/households/{hid}/requests/{device}/approve: {"epoch","body"}, the household key at its current epoch sealed
 * for the waiting PC (HouseholdCrypto.WrapFor, from the approving member). In one step the PC becomes a member, its
 * envelope is kept and its request is done; a full household leaves all three as they were. Any epoch but the current
 * one is 409, and so is an envelope the PC already has at it: one is never overwritten.
 */
export async function handleApprove(env: Cloudflare.Env, member: MemberRow, device: string, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  if (!isEpoch(posted?.epoch) || !isEnvelopeBody(posted?.body)) {
    return errorResponse(400, 'The body must be {"epoch","body"}: the household key sealed for that PC.');
  }

  const now = Date.now();
  const request = await env.DB.prepare(`SELECT r.sign_key, r.dh_key ${WAITING_REQUEST} AND r.device = ?`)
    .bind(member.household, now - JOIN_REQUEST_LIFETIME_MS, device)
    .first<{ sign_key: string; dh_key: string }>();
  if (!request) return errorResponse(404, "That PC isn't waiting to join this household.");

  // An approver that hasn't fetched the current key would hand over one the newer batches can't be opened with.
  const epoch = await currentEpoch(env, member.household);
  if (posted.epoch !== epoch) return errorResponse(409, `The household's key is at epoch ${epoch}; approve with that one.`);
  const sealedAlready = await env.DB.prepare("SELECT 1 FROM key_envelopes WHERE household = ? AND epoch = ? AND device = ?")
    .bind(member.household, epoch, device)
    .first();
  if (sealedAlready) return errorResponse(409, "That PC already has a key at this epoch.");

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
      `DELETE FROM join_requests WHERE household = ?1 AND device = ?2
       AND EXISTS (SELECT 1 FROM members WHERE household = ?1 AND device = ?2 AND removed IS NULL)`,
    ).bind(member.household, device),
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

  const row = await env.DB.prepare(
    `SELECT r.verifier_hash, l.household, h.epoch AS current
     FROM recovery r JOIN account_households l ON l.account = r.account JOIN households h ON h.id = l.household
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
    env.DB.prepare("DELETE FROM recovery WHERE account = ?").bind(session.account),
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
