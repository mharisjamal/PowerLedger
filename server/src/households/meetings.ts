import { addressOf } from "../address";
import { readBounded } from "../body";
import { errorResponse, ok, overAddressLimit } from "./http";

/** Pairing by code (households design §4, plan 0.6): the two PCs meet at /v1/meetings/{id}/{slot}, where the ID is the
 * first 16 bytes of SHA-256 of the code. Nothing is signed; the slots' contents vouch for themselves with the code's
 * MAC or are sealed with the session key, so the Worker only keeps them, once each, for 10 minutes. */
export const MEETING_SLOTS = ["adder", "joiner", "answer", "welcome", "joined"] as const;
export const MAX_SLOT_BYTES = 8 * 1024;
export const MEETING_LIFETIME_MS = 10 * 60 * 1000;
/** Live meetings at most, the whole server's: past it, a new one waits (503). */
export const MAX_LIVE_MEETINGS = 5000;

/**
 * PUT /v1/meetings/{id}/{slot}: 8 KB at most, written once, within 10 minutes of the meeting's first PUT. Every PUT
 * clears the meetings that have ended. Starting a meeting has a per-address limit of its own (MEETING_LIMIT, 429), and
 * the server keeps at most 5000 live (503).
 */
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
  const ended = now - MEETING_LIFETIME_MS;
  await env.DB.prepare("DELETE FROM meetings WHERE created <= ?").bind(ended).run();
  if (meeting?.created != null && meeting.created <= ended) return errorResponse(410, "This meeting has ended.");

  const created = meeting?.created ?? now;
  if (meeting?.created == null) {
    const starting = await env.MEETING_LIMIT.limit({ key: addressOf(request) });
    if (!starting.success) return errorResponse(429, "Too many pairing codes from this address; wait a minute.");
    const live = await env.DB.prepare("SELECT COUNT(DISTINCT id) AS n FROM meetings WHERE created > ?")
      .bind(ended)
      .first<{ n: number }>();
    if ((live?.n ?? 0) >= MAX_LIVE_MEETINGS) return errorResponse(503, "The server is busy; try again later.");
  }

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
