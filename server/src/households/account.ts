import { sha256hex } from "../auth";
import { type MemberRow, type SessionRow, sessionToken, verifySession } from "./auth";
import { base64urlDecode } from "./encoding";
import { addMemberStatement, HOUSEHOLD_ID, isEnvelopeBody, isEpoch, MAX_MEMBERS, readSmall } from "./households";
import { errorResponse, ok, parseObject } from "./http";

/** At most this many PCs wait to join one household at a time. */
export const MAX_WAITING = 16;
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

/** POST /v1/account/household: links the account to the household this PC is in, or the one {"householdId"} names. An
 * account links to one household; linking it again to the same one is done. */
export async function handleLink(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const posted = body.byteLength === 0 ? {} : parseObject(body);
  if (!posted) return errorResponse(400, "The body must be a JSON object.");

  let household: string;
  const named = posted.householdId ?? posted.household;
  if (named !== undefined) {
    if (typeof named !== "string" || !HOUSEHOLD_ID.test(named)) {
      return errorResponse(400, "householdId must be 32 lower-case hex characters.");
    }
    if (!(await isCurrentMember(env, named, session.device))) return errorResponse(403, "This PC isn't a member of that household.");
    household = named;
  } else {
    const rows = await env.DB.prepare("SELECT household FROM members WHERE device = ? AND removed IS NULL")
      .bind(session.device)
      .all<{ household: string }>();
    if (rows.results.length === 0) return errorResponse(409, "This PC isn't in a household yet.");
    if (rows.results.length > 1) return errorResponse(400, "This PC is in more than one household: say which, as householdId.");
    household = rows.results[0].household;
  }

  await env.DB.prepare("INSERT INTO account_households (account, household, linked) VALUES (?, ?, ?) ON CONFLICT DO NOTHING")
    .bind(session.account, household, Date.now())
    .run();
  const linked = await linkOf(env, session.account);
  return linked === household ? ok({ householdId: household }) : errorResponse(409, "This account is linked to another household.");
}

/** POST /v1/account/requests: this PC, signed in as an account linked to a household it isn't in, asks to join it. A
 * member approves it (POST …/requests/{device}/approve). */
export async function handleAskToJoin(env: Cloudflare.Env, session: SessionRow): Promise<Response> {
  const household = await linkOf(env, session.account);
  if (!household) return errorResponse(409, "This account isn't linked to a household yet.");
  if (await isCurrentMember(env, household, session.device)) return errorResponse(409, "This PC is already in the household.");

  const waiting = await env.DB.prepare("SELECT COUNT(*) AS n FROM join_requests WHERE household = ? AND device != ?")
    .bind(household, session.device)
    .first<{ n: number }>();
  if ((waiting?.n ?? 0) >= MAX_WAITING) return errorResponse(409, "Too many PCs are already waiting to join this household.");

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

/** GET /v1/households/{hid}/requests: the PCs waiting to join, for a member to approve, with their keys. */
export async function handleListRequests(env: Cloudflare.Env, member: MemberRow): Promise<Response> {
  const rows = await env.DB.prepare(`SELECT r.device, r.sign_key, r.dh_key, r.created ${WAITING_REQUEST} ORDER BY r.created, r.device`)
    .bind(member.household, Date.now() - JOIN_REQUEST_LIFETIME_MS)
    .all<{ device: string; sign_key: string; dh_key: string; created: number }>();
  return Response.json(
    rows.results.map((row) => ({ device: row.device, sign: row.sign_key, dh: row.dh_key, created: row.created })),
  );
}

/**
 * POST /v1/households/{hid}/requests/{device}/approve: {"epoch","body"}, the household key sealed for the waiting PC
 * (HouseholdCrypto.WrapFor, from the approving member). In one step the PC becomes a member, its envelope is kept and
 * its request is done; a full household leaves all three as they were.
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

  // An approver that hasn't fetched the latest key yet would hand over one the newer batches can't be opened with.
  const latest = await env.DB.prepare("SELECT MAX(epoch) AS epoch FROM key_envelopes WHERE household = ?")
    .bind(member.household)
    .first<{ epoch: number | null }>();
  if (latest?.epoch != null && posted.epoch < latest.epoch) {
    return errorResponse(409, `The household's key is at epoch ${latest.epoch} now; approve with that one.`);
  }

  const alreadyIn = await isCurrentMember(env, member.household, device);
  const results = await env.DB.batch([
    ...(alreadyIn ? [] : [addMemberStatement(env, member.household, device, request.sign_key, request.dh_key, now)]),
    env.DB.prepare(
      `INSERT INTO key_envelopes (household, epoch, device, from_device, body, created)
       SELECT ?1, ?2, ?3, ?4, ?5, ?6
       WHERE EXISTS (SELECT 1 FROM members WHERE household = ?1 AND device = ?3 AND removed IS NULL)
       ON CONFLICT (household, epoch, device) DO UPDATE SET
         from_device = excluded.from_device, body = excluded.body, created = excluded.created`,
    ).bind(member.household, posted.epoch, device, member.device, posted.body, now),
    env.DB.prepare(
      `DELETE FROM join_requests WHERE household = ?1 AND device = ?2
       AND EXISTS (SELECT 1 FROM members WHERE household = ?1 AND device = ?2 AND removed IS NULL)`,
    ).bind(member.household, device),
  ]);
  if (!alreadyIn && results[0].results.length === 0) return errorResponse(409, `This household already has ${MAX_MEMBERS} PCs.`);
  return ok();
}

function isSealed(value: unknown, maxChars: number): value is string {
  if (typeof value !== "string" || value.length > maxChars) return false;
  const bytes = base64urlDecode(value);
  return bytes !== null && bytes.byteLength >= 28;
}

/**
 * PUT /v1/account/recovery: {"body","verifier","epoch"?} from a member of the linked household. The body is the household
 * key sealed under the recovery code's key; the verifier is 32 bytes the PC makes from the household key, which is what
 * POST /v1/account/recover checks its proof with, since the Worker never has the key. A later PUT replaces it.
 */
export async function handlePutRecovery(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  const verifier = typeof posted?.verifier === "string" ? base64urlDecode(posted.verifier) : null;
  const epoch = posted?.epoch ?? null;
  if (!isSealed(posted?.body, MAX_RECOVERY_CHARS)) return errorResponse(400, "body must be the sealed household key, as base64url.");
  if (!verifier || verifier.byteLength !== VERIFIER_BYTES) return errorResponse(400, "verifier must be 32 bytes, as base64url.");
  if (epoch !== null && !isEpoch(epoch)) return errorResponse(400, "epoch must be a whole number.");

  const household = await linkOf(env, session.account);
  if (!household) return errorResponse(409, "This account isn't linked to a household yet.");
  if (!(await isCurrentMember(env, household, session.device))) {
    return errorResponse(403, "Only a PC in the household can set how to recover it.");
  }

  await env.DB.prepare(
    `INSERT INTO recovery (account, body, verifier, epoch, updated) VALUES (?, ?, ?, ?, ?)
     ON CONFLICT (account) DO UPDATE SET
       body = excluded.body, verifier = excluded.verifier, epoch = excluded.epoch, updated = excluded.updated`,
  )
    .bind(session.account, posted!.body, posted!.verifier, epoch, Date.now())
    .run();
  return ok();
}

/** GET /v1/account/recovery: {"householdId","epoch","body"}, for any PC signed in as the account; never the verifier. */
export async function handleGetRecovery(env: Cloudflare.Env, session: SessionRow): Promise<Response> {
  const row = await env.DB.prepare(
    `SELECT r.body, r.epoch, l.household FROM recovery r LEFT JOIN account_households l ON l.account = r.account
     WHERE r.account = ?`,
  )
    .bind(session.account)
    .first<{ body: string; epoch: number | null; household: string | null }>();
  if (!row) return errorResponse(404, "This account has no recovery envelope.");
  return Response.json({ householdId: row.household, epoch: row.epoch, body: row.body });
}

/**
 * POST /v1/account/recover: {"proof"}, HMAC-SHA256 of this PC's device ID under the recovery verifier, which only a PC
 * that opened the recovery envelope can make. On the account's authority, with no approval, this PC becomes a member of
 * the linked household.
 */
export async function handleRecover(env: Cloudflare.Env, session: SessionRow, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  const proof = typeof posted?.proof === "string" ? base64urlDecode(posted.proof) : null;
  if (!proof || proof.byteLength !== 32) {
    return errorResponse(400, "proof must be HMAC-SHA256 of this PC's device ID under the recovery verifier, as base64url.");
  }

  const row = await env.DB.prepare(
    `SELECT r.verifier, l.household FROM recovery r JOIN account_households l ON l.account = r.account WHERE r.account = ?`,
  )
    .bind(session.account)
    .first<{ verifier: string; household: string }>();
  if (!row) return errorResponse(404, "This account has nothing to recover.");

  const key = await crypto.subtle.importKey("raw", base64urlDecode(row.verifier)!, { name: "HMAC", hash: "SHA-256" }, false, ["verify"]);
  if (!(await crypto.subtle.verify("HMAC", key, proof, new TextEncoder().encode(session.device)))) {
    return errorResponse(403, "The recovery proof doesn't match.");
  }

  if (!(await isCurrentMember(env, row.household, session.device))) {
    const added = await addMemberStatement(env, row.household, session.device, session.sign_key, session.dh_key, Date.now()).first();
    if (!added) return errorResponse(409, `This household already has ${MAX_MEMBERS} PCs.`);
  }
  await env.DB.prepare("DELETE FROM join_requests WHERE household = ? AND device = ?").bind(row.household, session.device).run();
  return ok({ householdId: row.household });
}

/** POST /v1/auth/signout: ends this PC's session, signed by the PC it was given to. A session already ended is done. */
export async function handleSignout(request: Request, env: Cloudflare.Env): Promise<Response> {
  const body = await readSmall(request);
  if (body instanceof Response) return body;
  const token = sessionToken(request);
  if (!token) return errorResponse(401, "This request needs a session.");

  const tokenHash = await sha256hex(token);
  const exists = await env.DB.prepare("SELECT 1 FROM sessions WHERE token_hash = ?").bind(tokenHash).first();
  if (!exists) return ok();

  const session = await verifySession(request, env, body);
  if (session instanceof Response) return session;
  await env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?").bind(tokenHash).run();
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
