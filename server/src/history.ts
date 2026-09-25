import { checkInstall, countRequest } from "./auth";
import { checkHours } from "./hours";
import { firstHistoryError } from "./schema";
import { deleteBodies, putBody } from "./store";
import { errorResponse, readUpload } from "./upload";
import { belowMinimum, updateRequired } from "./version";

const HOUR_MS = 3_600_000;
// Three years of history is 36 chunks; this leaves room for retries, on its own count, not the reports'.
const MAX_REQUESTS_PER_DAY = 60;

interface HistoryBody {
  schema: "history-v1";
  installId: string;
  app: string;
  consent: { version: number; diagnostics: boolean; usage: boolean; power: boolean; share: boolean };
  utcOffsetMinutes: number;
  hours: { t: number }[];
}

/**
 * POST /v1/history (Plan Q spec §2): one chunk of the hourly totals already on a PC when its user agreed on consent
 * version 2 with Hardware and power on, at most 31 days of them, sent once. The same checks as /v1/report; the body
 * is stored as sent at `history/v1/<installId>/<first hour's t>.json.gz` and indexed in `histories`, so a chunk sent
 * again replaces the first.
 */
export async function handleHistory(request: Request, env: Cloudflare.Env): Promise<Response> {
  // 1 to 4. The bearer key, the per-address limit, gzip and its size limits, and JSON (upload.ts).
  const upload = await readUpload(request, env);
  if (!upload.ok) return upload.response;
  const { key, body, value: parsed } = upload;

  // 5. The schema: history-v1, on consent version 2 or later with power on.
  const schemaError = firstHistoryError(parsed);
  if (schemaError) return errorResponse(400, `${schemaError.instanceLocation} ${schemaError.error}`);
  const history = parsed as HistoryBody;

  // 5a. The app that sent it must be at least the minimum version, as its header (checked in readUpload) must.
  if (belowMinimum(history.app, env)) return updateRequired(env);

  // 6. The hours, left to hand-written code to keep a chunk inside the CPU limit.
  const problem = checkHours(history.hours, Date.now());
  if (problem) return errorResponse(400, problem);

  // 7. The install's identity.
  const state = await checkInstall(env, history.installId, key);
  if (state === "gone") return errorResponse(410, "This install's data has been deleted.");
  if (state === "mismatch") return errorResponse(403, "This install's key does not match.");

  // 8. The daily request budget, per install, history's own.
  const count = await countRequest(env, history.installId, "history");
  if (count > MAX_REQUESTS_PER_DAY) return errorResponse(429, "Too many requests from this install today.");

  // 9. Store the body as sent, and index it by the chunk's first hour and the end of its last.
  const fromMs = history.hours[0].t;
  const toMs = history.hours[history.hours.length - 1].t + HOUR_MS;
  const r2Key = `history/v1/${history.installId}/${fromMs}.json.gz`;
  const country = request.cf?.country ? String(request.cf.country) : "XX";
  const receivedAt = Date.now();

  await putBody(env, r2Key, body, { contentType: "application/json", receivedAt });

  await env.DB.prepare(
    `INSERT INTO histories (install_id, from_ms, to_ms, received_at, bytes, country, r2_key)
     VALUES (?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (install_id, from_ms) DO UPDATE SET
       to_ms = excluded.to_ms, received_at = excluded.received_at, bytes = excluded.bytes,
       country = excluded.country, r2_key = excluded.r2_key`,
  )
    .bind(history.installId, fromMs, toMs, receivedAt, body.byteLength, country, r2Key)
    .run();

  // checkInstall() guarantees a row already exists (created on first use, or already there).
  await env.DB.prepare(
    `UPDATE installs SET consent_version = ?, diagnostics = ?, usage = ?, power = ?, share = ?,
       last_seen = ?, country = ?, app_version = ? WHERE id = ?`,
  )
    .bind(
      history.consent.version,
      history.consent.diagnostics ? 1 : 0,
      history.consent.usage ? 1 : 0,
      history.consent.power ? 1 : 0,
      history.consent.share ? 1 : 0,
      receivedAt,
      country,
      history.app,
      history.installId,
    )
    .run();

  // 10. As for a report: a delete that landed meanwhile has swept the install, so take back what this request added.
  const tombstone = await env.DB.prepare("SELECT 1 FROM tombstones WHERE id = ?").bind(history.installId).first();
  if (tombstone) {
    await deleteBodies(env, [r2Key]);
    await env.DB.batch([
      env.DB.prepare("DELETE FROM histories WHERE install_id = ? AND from_ms = ?").bind(history.installId, fromMs),
      env.DB.prepare("DELETE FROM installs WHERE id = ?").bind(history.installId),
    ]);
    return errorResponse(410, "This install's data has been deleted.");
  }

  return Response.json({ ok: true });
}
