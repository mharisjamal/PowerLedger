import { readBounded } from "../body";
import { deleteBodies, getBody, putBody } from "../store";
import { type MemberRow } from "./auth";
import { base64urlDecode, base64urlEncode } from "./encoding";
import { isEpoch } from "./households";
import { errorResponse, isWholeNumber, ok, parseObject } from "./http";

/** A batch as posted, at most (households design §8). */
export const MAX_BATCH_BYTES = 1_048_576;
export const MAX_PAGE_ITEMS = 100;
/** A page stops before its sealed bodies pass this, so a page of big batches stays a sensible size. */
export const MAX_PAGE_BYTES = 4 * 1024 * 1024;
/** Nonce and tag: the least HouseholdCrypto.Seal gives. */
const MIN_SEALED_BYTES = 28;
const WHOLE_NUMBER = /^[0-9]{1,15}$/;

export async function readBatch(request: Request): Promise<Uint8Array | Response> {
  return (await readBounded(request, MAX_BATCH_BYTES)) ?? errorResponse(413, "A batch is at most 1 MB.");
}

/**
 * POST /v1/households/{hid}/batches: {"device","epoch","seq","body"} (plan 0.6), where seq is the sender's own sequence
 * number, part of the sealed body's associated data. The Worker numbers the household's batches in arrival order, max +
 * 1, which is what GET's cursor counts; the sealed body goes to the body store.
 */
export async function handlePostBatch(env: Cloudflare.Env, member: MemberRow, body: Uint8Array): Promise<Response> {
  const posted = parseObject(body);
  if (posted?.device !== member.device) return errorResponse(400, "device must be this PC's own ID.");
  if (!isEpoch(posted.epoch)) return errorResponse(400, "epoch must be a whole number.");
  if (!isWholeNumber(posted.seq)) return errorResponse(400, "seq must be a whole number.");
  const sealedBytes = typeof posted.body === "string" ? base64urlDecode(posted.body) : null;
  if (!sealedBytes || sealedBytes.byteLength < MIN_SEALED_BYTES) {
    return errorResponse(400, "body must be the sealed batch, as base64url.");
  }

  // The body first, under a key of its own: a row is never seen before its body is there.
  const received = Date.now();
  const key = `batches/v1/${member.household}/${crypto.randomUUID()}`;
  await putBody(env, key, sealedBytes, { contentType: "application/octet-stream", receivedAt: received });
  try {
    await env.DB.prepare(
      `INSERT INTO batches (household, seq, device, epoch, device_seq, bytes, received, r2_key)
       SELECT ?1, COALESCE(MAX(seq), 0) + 1, ?2, ?3, ?4, ?5, ?6, ?7 FROM batches WHERE household = ?1`,
    )
      .bind(member.household, member.device, posted.epoch, posted.seq, sealedBytes.byteLength, received, key)
      .run();
  } catch (error) {
    await deleteBodies(env, [key]);
    throw error;
  }
  return ok();
}

interface BatchRow {
  seq: number;
  device: string;
  epoch: number;
  device_seq: number;
  bytes: number;
  r2_key: string;
}

/**
 * GET /v1/households/{hid}/batches?after=<cursor>&limit=<1..100>: the other members' batches after the cursor, oldest
 * first, as {"items":[{"seq","device","epoch","body"}],"next","more"}. Each item is its batch as posted, so seq is the
 * sender's own; next is the cursor to send as after next time (after itself when nothing new came), and more says
 * another page is already waiting.
 */
export async function handleGetBatches(
  env: Cloudflare.Env,
  member: MemberRow,
  url: URL,
  pageBytes = MAX_PAGE_BYTES,
): Promise<Response> {
  const afterText = url.searchParams.get("after") || "0";
  const limitText = url.searchParams.get("limit") || String(MAX_PAGE_ITEMS);
  if (!WHOLE_NUMBER.test(afterText) || !WHOLE_NUMBER.test(limitText)) {
    return errorResponse(400, "after and limit must be whole numbers.");
  }
  const after = Number(afterText);
  const limit = Math.min(Math.max(Number(limitText), 1), MAX_PAGE_ITEMS);

  const rows = await env.DB.prepare(
    `SELECT seq, device, epoch, device_seq, bytes, r2_key FROM batches
     WHERE household = ? AND seq > ? AND device != ? ORDER BY seq LIMIT ?`,
  )
    .bind(member.household, after, member.device, limit + 1)
    .all<BatchRow>();

  const items: { seq: number; device: string; epoch: number; body: string }[] = [];
  let next = after;
  let more = rows.results.length > limit;
  let total = 0;
  for (const row of rows.results.slice(0, limit)) {
    if (items.length > 0 && total + row.bytes > pageBytes) {
      more = true;
      break;
    }
    next = row.seq;
    const sealedBytes = await getBody(env, row.r2_key);
    if (!sealedBytes) continue; // Retention took it between the query and now.
    total += sealedBytes.byteLength;
    items.push({ seq: row.device_seq, device: row.device, epoch: row.epoch, body: base64urlEncode(sealedBytes) });
  }

  return Response.json({ items, next, more });
}
