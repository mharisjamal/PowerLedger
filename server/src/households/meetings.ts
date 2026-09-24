import { readBounded } from "../body";
import { addressOf, errorResponse, ok } from "./http";

/** Pairing by code (households design §4, plan 0.6): the two PCs meet at /v1/meetings/{id}/{slot}, where the ID is the
 * first 16 bytes of SHA-256 of the code. Nothing is signed; the slots' contents vouch for themselves with the code's
 * MAC or are sealed with the session key, so the Worker only keeps them, once each, for 10 minutes. */
export const MEETING_SLOTS = ["adder", "joiner", "answer", "welcome"] as const;
export const MAX_SLOT_BYTES = 8 * 1024;
export const MEETING_LIFETIME_MS = 10 * 60 * 1000;

async function overAddressLimit(request: Request, env: Cloudflare.Env): Promise<Response | null> {
  const limited = await env.ADDRESS_LIMIT.limit({ key: addressOf(request) });
  return limited.success ? null : errorResponse(429, "Too many requests from this address.");
}

/** PUT /v1/meetings/{id}/{slot}: 8 KB at most, written once, within 10 minutes of the meeting's first PUT. */
export async function handlePutSlot(
  request: Request,
  env: Cloudflare.Env,
  id: string,
  slot: string,
  now = Date.now(),
): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;

  const body = await readBounded(request, MAX_SLOT_BYTES);
  if (body === null) return errorResponse(413, "A meeting slot holds at most 8 KB.");
  if (body.byteLength === 0) return errorResponse(400, "A meeting slot can't be empty.");

  const meeting = await env.DB.prepare("SELECT MIN(created) AS created FROM meetings WHERE id = ?")
    .bind(id)
    .first<{ created: number | null }>();
  const created = meeting?.created ?? now;
  if (now - created >= MEETING_LIFETIME_MS) return errorResponse(410, "This meeting has ended.");

  const written = await env.DB.prepare(
    "INSERT INTO meetings (id, slot, body, created) VALUES (?, ?, ?, ?) ON CONFLICT DO NOTHING RETURNING slot",
  )
    .bind(id, slot, body, created)
    .first();
  return written ? ok() : errorResponse(409, "This slot has already been written.");
}

/** GET /v1/meetings/{id}/{slot}: the bytes as put, or 404 while the slot is empty or once the meeting has ended. */
export async function handleGetSlot(
  request: Request,
  env: Cloudflare.Env,
  id: string,
  slot: string,
  now = Date.now(),
): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;

  const row = await env.DB.prepare("SELECT body, created FROM meetings WHERE id = ? AND slot = ?")
    .bind(id, slot)
    .first<{ body: ArrayBuffer; created: number }>();
  if (!row || now - row.created >= MEETING_LIFETIME_MS) return errorResponse(404, "Nothing is in this slot.");
  return new Response(new Uint8Array(row.body), { headers: { "Content-Type": "application/octet-stream" } });
}
