import { readBounded } from "../body";
import { deleteBodies, getBodies, putBody } from "../store";
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

/** What one PC may post in a UTC day: a year's backfill fits many times over, a flood doesn't. */
export const MAX_BATCHES_PER_DAY = 200;
export const MAX_BATCH_BYTES_PER_DAY = 5 * 1024 * 1024;
/** What the whole server takes in a UTC day, a safety cap on storage whatever happens. */
export const MAX_TOTAL_BATCH_BYTES_PER_DAY = 2 * 1024 * 1024 * 1024;
const BUSY = "The server is busy; try again later.";

/**
 * Counts a batch of `size` sealed bytes against the PC's day (429 past 200 batches or 5 MB) and then the server's (503
 * past 2 GB, giving the PC's count back); null when both have room.
 */
async function countBatch(env: Cloudflare.Env, device: string, size: number, day: string): Promise<Response | null> {
  const mine = await env.DB.prepare(
    `INSERT INTO device_requests (device, utc_day, count, batches, batch_bytes) VALUES (?1, ?2, 0, 1, ?3)
     ON CONFLICT (device, utc_day) DO UPDATE SET
       batches = device_requests.batches + 1, batch_bytes = device_requests.batch_bytes + excluded.batch_bytes
     WHERE device_requests.batches < ?4 AND device_requests.batch_bytes + excluded.batch_bytes <= ?5
     RETURNING batches`,
  )
    .bind(device, day, size, MAX_BATCHES_PER_DAY, MAX_BATCH_BYTES_PER_DAY)
    .first();
  if (!mine) return errorResponse(429, "This PC has sent as many batches as it can today.");

  const all = await env.DB.prepare(
    `INSERT INTO daily_totals (utc_day, batch_bytes) VALUES (?1, ?2)
     ON CONFLICT (utc_day) DO UPDATE SET batch_bytes = daily_totals.batch_bytes + excluded.batch_bytes
     WHERE daily_totals.batch_bytes + excluded.batch_bytes <= ?3
     RETURNING batch_bytes`,
  )
    .bind(day, size, MAX_TOTAL_BATCH_BYTES_PER_DAY)
    .first();
  if (all) return null;

  await env.DB.prepare(
    "UPDATE device_requests SET batches = batches - 1, batch_bytes = batch_bytes - ?3 WHERE device = ?1 AND utc_day = ?2",
  )
    .bind(device, day, size)
    .run();
  return errorResponse(503, BUSY);
}

export async function readBatch(request: Request): Promise<Uint8Array | Response> {
  return (await readBounded(request, MAX_BATCH_BYTES)) ?? errorResponse(413, "A batch is at most 1 MB.");
}

/**
 * POST /v1/households/{hid}/batches: {"device","epoch","seq","body"} (plan 0.6), where seq is the sender's own sequence
 * number, part of the sealed body's associated data. The Worker numbers the household's batches in arrival order, max +
 * 1, which is what GET's cursor counts; the sealed body goes to the body store. Each PC may post 200 batches and 5 MB a
 * UTC day (429), and the server takes 2 GB a day in all (503).
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

  const received = Date.now();
  const refused = await countBatch(env, member.device, sealedBytes.byteLength, new Date(received).toISOString().slice(0, 10));
  if (refused) return refused;

  // The body first, under a key of its own: a row is never seen before its body is there.
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

  // The page is settled from the rows' sizes, then its bodies are read in one go.
  const page: BatchRow[] = [];
  let more = rows.results.length > limit;
  let total = 0;
  for (const row of rows.results.slice(0, limit)) {
    if (page.length > 0 && total + row.bytes > pageBytes) {
      more = true;
      break;
    }
    page.push(row);
    total += row.bytes;
  }
  const bodies = await getBodies(env, page.map((row) => row.r2_key));

  const items = page
    .filter((row) => bodies.has(row.r2_key)) // Retention may take one between the query and now.
    .map((row) => ({ seq: row.device_seq, device: row.device, epoch: row.epoch, body: base64urlEncode(bodies.get(row.r2_key)!) }));
  const next = page.length > 0 ? page[page.length - 1].seq : after;
  return Response.json({ items, next, more });
}
