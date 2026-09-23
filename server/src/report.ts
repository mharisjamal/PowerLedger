import { bearer, checkInstall, countRequest } from "./auth";
import { readBounded } from "./body";
import { dayInRange } from "./day";
import { gunzipBounded } from "./gzip";
import { checkMinutes } from "./minutes";
import { firstSchemaError } from "./schema";

const MAX_BODY_BYTES = 1_048_576;
const MAX_UNPACKED_BYTES = 8 * 1024 * 1024;
const MAX_REQUESTS_PER_DAY = 20;

interface ReportBody {
  schema: 1;
  installId: string;
  app: string;
  day: string;
  consent: { version: number; diagnostics: boolean; usage: boolean; power: boolean; share: boolean };
  diagnostics?: unknown;
  usage?: unknown;
  power?: { hardware: unknown; minutes: Record<string, unknown> };
}

function errorResponse(status: number, message: string): Response {
  return Response.json({ error: message }, { status });
}

export async function handleReport(request: Request, env: Cloudflare.Env): Promise<Response> {
  // 1. Auth: a well-formed bearer key is required up front.
  const key = bearer(request);
  if (!key) return errorResponse(401, "A valid bearer key is required.");

  // 2. Per-address rate limit, before reading anything.
  const address = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const limited = await env.ADDRESS_LIMIT.limit({ key: address });
  if (!limited.success) return errorResponse(429, "Too many requests from this address.");

  // 3. Content-Encoding and the as-sent size limit, counted while reading so an oversized body is never held whole.
  if (request.headers.get("Content-Encoding") !== "gzip") {
    return errorResponse(413, "The body must be sent gzip-encoded.");
  }
  const body = await readBounded(request, MAX_BODY_BYTES);
  if (body === null) return errorResponse(413, "The body is larger than 1 MB.");

  // 4. Bounded decompression, then JSON.
  let unpacked: Uint8Array | null;
  try {
    unpacked = await gunzipBounded(body, MAX_UNPACKED_BYTES);
  } catch {
    return errorResponse(400, "The body is not valid gzip.");
  }
  if (unpacked === null) return errorResponse(413, "The unpacked body is larger than 8 MB.");

  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder().decode(unpacked));
  } catch {
    return errorResponse(400, "The body is not valid JSON.");
  }

  // 5. The schema.
  const schemaError = firstSchemaError(parsed);
  if (schemaError) return errorResponse(400, `${schemaError.instanceLocation} ${schemaError.error}`);
  const report = parsed as ReportBody;

  // 6. The minutes, left to hand-written code to keep a report inside the CPU limit.
  if (report.power) {
    const problem = checkMinutes(report.power.minutes);
    if (problem) return errorResponse(400, problem);
  }

  // 7. The day must be recent.
  if (!dayInRange(report.day, new Date())) {
    return errorResponse(400, "day must be within the last 15 days, or tomorrow.");
  }

  // 8. The install's identity.
  const state = await checkInstall(env, report.installId, key);
  if (state === "gone") return errorResponse(410, "This install's data has been deleted.");
  if (state === "mismatch") return errorResponse(403, "This install's key does not match.");

  // 9. The daily request budget, per install.
  const count = await countRequest(env, report.installId);
  if (count > MAX_REQUESTS_PER_DAY) return errorResponse(429, "Too many requests from this install today.");

  // 10. Store the body as sent, and index it. A day sent again replaces the first, since the R2
  // key and the reports row are both keyed by (install, day).
  const r2Key = `reports/v1/${report.installId}/${report.day}.json.gz`;
  const country = request.cf?.country ? String(request.cf.country) : "XX";
  const receivedAt = Date.now();
  const sections = (["diagnostics", "usage", "power"] as const).filter((name) => name in report).join(",");

  await env.REPORTS.put(r2Key, body, {
    httpMetadata: { contentType: "application/json", contentEncoding: "gzip" },
    customMetadata: { country, receivedAt: String(receivedAt), app: report.app },
  });

  await env.DB.prepare(
    `INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key)
     VALUES (?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (install_id, day) DO UPDATE SET
       received_at = excluded.received_at, bytes = excluded.bytes, sections = excluded.sections,
       country = excluded.country, r2_key = excluded.r2_key`,
  )
    .bind(report.installId, report.day, receivedAt, body.byteLength, sections, country, r2Key)
    .run();

  // checkInstall() guarantees a row already exists (created on first use, or already there).
  await env.DB.prepare(
    `UPDATE installs SET consent_version = ?, diagnostics = ?, usage = ?, power = ?, share = ?,
       last_seen = ?, country = ?, app_version = ? WHERE id = ?`,
  )
    .bind(
      report.consent.version,
      report.consent.diagnostics ? 1 : 0,
      report.consent.usage ? 1 : 0,
      report.consent.power ? 1 : 0,
      report.consent.share ? 1 : 0,
      receivedAt,
      country,
      report.app,
      report.installId,
    )
    .run();

  // 11. A delete that landed while this report was being stored has already swept the install; take back what this
  // request added after the sweep, so a deleted install keeps nothing.
  if (await discardIfDeleted(env, report.installId, report.day, r2Key)) {
    return errorResponse(410, "This install's data has been deleted.");
  }

  return Response.json({ ok: true });
}

/** Deletes one day's object and row when the install has a tombstone; true when it did. */
export async function discardIfDeleted(env: Cloudflare.Env, installId: string, day: string, r2Key: string): Promise<boolean> {
  const tombstone = await env.DB.prepare("SELECT 1 FROM tombstones WHERE id = ?").bind(installId).first();
  if (!tombstone) return false;
  await env.REPORTS.delete(r2Key);
  await env.DB.batch([
    env.DB.prepare("DELETE FROM reports WHERE install_id = ? AND day = ?").bind(installId, day),
    env.DB.prepare("DELETE FROM installs WHERE id = ?").bind(installId),
  ]);
  return true;
}
