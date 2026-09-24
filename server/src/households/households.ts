import { deviceIdOf, DEVICE_ID, importSignKey, isDhKey, type MemberRow, verifySignedByKey } from "./auth";
import { readBounded } from "../body";
import { deleteBodies } from "../store";
import { base64urlDecode } from "./encoding";
import { errorResponse, isWholeNumber, ok, parseObject } from "./http";

export const HOUSEHOLD_ID = /^[0-9a-f]{32}$/;
/** Households design §8. */
export const MAX_MEMBERS = 16;
/** The body limit for every household request but a batch. */
export const SMALL_BODY_BYTES = 16 * 1024;
const MAX_ENVELOPE_CHARS = 1024;
const MAX_EPOCH = 2_147_483_647;

const BAD_KEYS = "sign and dh must be P-256 public keys, as base64url SubjectPublicKeyInfo.";

/** Reads a body of at most 16 KB, or the 413 to send back. */
export async function readSmall(request: Request): Promise<Uint8Array | Response> {
  return (await readBounded(request, SMALL_BODY_BYTES)) ?? errorResponse(413, "The body is larger than 16 KB.");
}

/** A posted {"sign","dh"} pair, checked; or the 400 to send back. */
async function readKeys(posted: Record<string, unknown> | null): Promise<{ sign: string; dh: string } | Response> {
  const sign = posted?.sign;
  const dh = posted?.dh;
  if (typeof sign !== "string" || typeof dh !== "string" || !(await importSignKey(sign)) || !(await isDhKey(dh))) {
    return errorResponse(400, BAD_KEYS);
  }
  return { sign, dh };
}

/** POST /v1/households: {"id","sign","dh"}, signed by that signing key, whose PC becomes the one member. */
export async function handleCreateHousehold(request: Request, env: Cloudflare.Env): Promise<Response> {
  const body = await readSmall(request);
  if (body instanceof Response) return body;

  const posted = parseObject(body);
  const id = posted?.id;
  if (typeof id !== "string" || !HOUSEHOLD_ID.test(id)) return errorResponse(400, "id must be 32 lower-case hex characters.");
  const keys = await readKeys(posted);
  if (keys instanceof Response) return keys;

  const signer = await verifySignedByKey(request, env, body, keys.sign);
  if (signer instanceof Response) return signer;

  const now = Date.now();
  const [created] = await env.DB.batch([
    env.DB.prepare("INSERT INTO households (id, created) VALUES (?, ?) ON CONFLICT DO NOTHING RETURNING id").bind(id, now),
    env.DB.prepare(
      `INSERT INTO members (household, device, sign_key, dh_key, added, removed)
       SELECT ?1, ?2, ?3, ?4, ?5, NULL WHERE NOT EXISTS (SELECT 1 FROM members WHERE household = ?1)`,
    ).bind(id, signer.device, keys.sign, keys.dh, now),
  ]);
  if (created.results.length > 0) return ok();

  // Already there: done when it's this PC's own household (a retry), taken otherwise.
  const mine = await env.DB.prepare("SELECT 1 FROM members WHERE household = ? AND device = ? AND removed IS NULL")
    .bind(id, signer.device)
    .first();
  return mine ? ok() : errorResponse(409, "A household with this ID already exists.");
}

/** Adds a PC as a current member, or makes a removed one current again, while the household has fewer than 16; false
 * when it's full. */
export function addMemberStatement(
  env: Cloudflare.Env,
  household: string,
  device: string,
  sign: string,
  dh: string,
  now: number,
): D1PreparedStatement {
  return env.DB.prepare(
    `INSERT INTO members (household, device, sign_key, dh_key, added, removed)
     SELECT ?1, ?2, ?3, ?4, ?5, NULL
     WHERE (SELECT COUNT(*) FROM members WHERE household = ?1 AND removed IS NULL) < ?6
     ON CONFLICT (household, device) DO UPDATE SET
       sign_key = excluded.sign_key, dh_key = excluded.dh_key, added = excluded.added, removed = NULL
     RETURNING device`,
  ).bind(household, device, sign, dh, now, MAX_MEMBERS);
}

/** POST /v1/households/{hid}/members: {"sign","dh"}, by a member. */
export async function handleAddMember(env: Cloudflare.Env, member: MemberRow, body: Uint8Array): Promise<Response> {
  const keys = await readKeys(parseObject(body));
  if (keys instanceof Response) return keys;
  const device = await deviceIdOf(base64urlDecode(keys.sign)!);

  const current = await env.DB.prepare("SELECT 1 FROM members WHERE household = ? AND device = ? AND removed IS NULL")
    .bind(member.household, device)
    .first();
  if (current) return ok();

  const added = await addMemberStatement(env, member.household, device, keys.sign, keys.dh, Date.now()).first();
  return added ? ok() : errorResponse(409, `This household already has ${MAX_MEMBERS} PCs.`);
}

/** DELETE /v1/households/{hid}/members/{device}: a member removes another, or itself to leave. The household ends with
 * its last member. */
export async function handleRemoveMember(env: Cloudflare.Env, member: MemberRow, device: string): Promise<Response> {
  const removed = await env.DB.prepare(
    "UPDATE members SET removed = ? WHERE household = ? AND device = ? AND removed IS NULL RETURNING device",
  )
    .bind(Date.now(), member.household, device)
    .first();
  if (!removed) return errorResponse(404, "That PC isn't a member of this household.");

  const left = await env.DB.prepare("SELECT COUNT(*) AS n FROM members WHERE household = ? AND removed IS NULL")
    .bind(member.household)
    .first<{ n: number }>();
  if (left?.n === 0) await endHousehold(env, member.household);

  return ok();
}

/** Deletes everything kept for a household: its batches and their bodies, key envelopes, members and itself. */
export async function endHousehold(env: Cloudflare.Env, household: string): Promise<void> {
  const bodies = await env.DB.prepare("SELECT r2_key FROM batches WHERE household = ?").bind(household).all<{ r2_key: string }>();
  await deleteBodies(env, bodies.results.map((row) => row.r2_key));
  await env.DB.batch([
    env.DB.prepare("DELETE FROM batches WHERE household = ?").bind(household),
    env.DB.prepare("DELETE FROM key_envelopes WHERE household = ?").bind(household),
    env.DB.prepare("DELETE FROM members WHERE household = ?").bind(household),
    env.DB.prepare("DELETE FROM households WHERE id = ?").bind(household),
  ]);
}

/** GET /v1/households/{hid}/members: every PC that is or was a member, times in unix ms. */
export async function handleListMembers(env: Cloudflare.Env, member: MemberRow): Promise<Response> {
  const rows = await env.DB.prepare(
    "SELECT device, sign_key, dh_key, added, removed FROM members WHERE household = ? ORDER BY added, device",
  )
    .bind(member.household)
    .all<{ device: string; sign_key: string; dh_key: string; added: number; removed: number | null }>();
  return Response.json(
    rows.results.map((row) => ({ device: row.device, sign: row.sign_key, dh: row.dh_key, added: row.added, removed: row.removed })),
  );
}

interface Envelope {
  device: string;
  body: string;
}

/** An envelope's body: base64url of what HouseholdCrypto.WrapFor sealed, 28 bytes at least. */
export function isEnvelopeBody(value: unknown): value is string {
  if (typeof value !== "string" || value.length > MAX_ENVELOPE_CHARS) return false;
  const bytes = base64urlDecode(value);
  return bytes !== null && bytes.byteLength >= 28;
}

export function isEpoch(value: unknown): value is number {
  return isWholeNumber(value, MAX_EPOCH);
}

/** POST /v1/households/{hid}/keys: {"epoch","envelopes":[{"device","body"}]}, the household key of a new epoch sealed to
 * current members. An epoch takes its keys once, and only when it's later than every epoch already here. */
export async function handlePostKeys(env: Cloudflare.Env, member: MemberRow, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  const epoch = posted?.epoch;
  const envelopes = posted?.envelopes;
  if (!isEpoch(epoch)) return errorResponse(400, "epoch must be a whole number.");
  if (
    !Array.isArray(envelopes) || envelopes.length === 0 || envelopes.length > MAX_MEMBERS ||
    !envelopes.every(
      (item): item is Envelope =>
        item !== null && typeof item === "object" && typeof item.device === "string" && DEVICE_ID.test(item.device) &&
        isEnvelopeBody(item.body),
    ) ||
    new Set(envelopes.map((item) => item.device)).size !== envelopes.length
  ) {
    return errorResponse(400, `envelopes must be 1 to ${MAX_MEMBERS} of {"device","body"}, one per PC.`);
  }

  const current = await env.DB.prepare("SELECT device FROM members WHERE household = ? AND removed IS NULL")
    .bind(member.household)
    .all<{ device: string }>();
  const currentIds = new Set(current.results.map((row) => row.device));
  if (!envelopes.every((item) => currentIds.has(item.device))) {
    return errorResponse(400, "Every envelope must be for a current member.");
  }

  const existing = await env.DB.prepare("SELECT device, body FROM key_envelopes WHERE household = ? AND epoch = ?")
    .bind(member.household, epoch)
    .all<Envelope>();
  if (existing.results.length > 0) {
    const same =
      existing.results.length === envelopes.length &&
      envelopes.every((item) => existing.results.some((row) => row.device === item.device && row.body === item.body));
    return same ? ok() : errorResponse(409, `Epoch ${epoch} already has its keys.`);
  }

  const latest = await env.DB.prepare("SELECT MAX(epoch) AS epoch FROM key_envelopes WHERE household = ?")
    .bind(member.household)
    .first<{ epoch: number | null }>();
  if (latest?.epoch != null && epoch <= latest.epoch) {
    return errorResponse(409, `Keys for epoch ${latest.epoch} are already here; a new epoch must be later.`);
  }

  const now = Date.now();
  try {
    await env.DB.batch(
      envelopes.map((item) =>
        env.DB.prepare(
          "INSERT INTO key_envelopes (household, epoch, device, from_device, body, created) VALUES (?, ?, ?, ?, ?, ?)",
        ).bind(member.household, epoch, item.device, member.device, item.body, now),
      ),
    );
  } catch {
    // Another member's keys for this epoch landed first.
    return errorResponse(409, `Epoch ${epoch} already has its keys.`);
  }
  return ok();
}

/** GET /v1/households/{hid}/keys/{epoch}: the caller's own envelope, and who sealed it. */
export async function handleGetKey(env: Cloudflare.Env, member: MemberRow, epoch: number): Promise<Response> {
  const row = await env.DB.prepare(
    "SELECT epoch, from_device, body FROM key_envelopes WHERE household = ? AND epoch = ? AND device = ?",
  )
    .bind(member.household, epoch, member.device)
    .first<{ epoch: number; from_device: string; body: string }>();
  if (!row) return errorResponse(404, "There's no key for this PC at that epoch.");
  return Response.json({ epoch: row.epoch, from: row.from_device, body: row.body });
}
