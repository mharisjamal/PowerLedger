import { bearer, checkInstall, countRequest } from "./auth";
import { readBounded } from "./body";
import { firstConsentError, GUID_PATTERN } from "./schema";

const MAX_BODY_BYTES = 4096;
const MAX_REQUESTS_PER_DAY = 20;

function errorResponse(status: number, message: string): Response {
  return Response.json({ error: message }, { status });
}

type SmallJson = { ok: true; value: unknown } | { ok: false; response: Response };

async function readSmallJson(request: Request, env: Cloudflare.Env): Promise<SmallJson> {
  const address = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const limited = await env.ADDRESS_LIMIT.limit({ key: address });
  if (!limited.success) return { ok: false, response: errorResponse(429, "Too many requests from this address.") };
  const bytes = await readBounded(request, MAX_BODY_BYTES);
  if (bytes === null) {
    return { ok: false, response: errorResponse(413, "The body is larger than 4 KB.") };
  }
  try {
    return { ok: true, value: JSON.parse(new TextDecoder().decode(bytes)) };
  } catch {
    return { ok: false, response: errorResponse(400, "The body is not valid JSON.") };
  }
}

export async function handleConsent(request: Request, env: Cloudflare.Env): Promise<Response> {
  const key = bearer(request);
  if (!key) return errorResponse(401, "A valid bearer key is required.");

  const parsed = await readSmallJson(request, env);
  if (!parsed.ok) return parsed.response;

  const schemaError = firstConsentError(parsed.value);
  if (schemaError) return errorResponse(400, `${schemaError.instanceLocation} ${schemaError.error}`);

  const { installId, consent } = parsed.value as {
    installId: string;
    consent: { version: number; diagnostics: boolean; usage: boolean; power: boolean; share: boolean };
  };

  const state = await checkInstall(env, installId, key);
  if (state === "gone") return errorResponse(410, "This install's data has been deleted.");
  if (state === "mismatch") return errorResponse(403, "This install's key does not match.");

  const count = await countRequest(env, installId);
  if (count > MAX_REQUESTS_PER_DAY) return errorResponse(429, "Too many requests from this install today.");

  await env.DB.prepare(
    `UPDATE installs SET consent_version = ?, diagnostics = ?, usage = ?, power = ?, share = ?, last_seen = ?
     WHERE id = ?`,
  )
    .bind(
      consent.version,
      consent.diagnostics ? 1 : 0,
      consent.usage ? 1 : 0,
      consent.power ? 1 : 0,
      consent.share ? 1 : 0,
      Date.now(),
      installId,
    )
    .run();

  return Response.json({ ok: true });
}

export async function handleDelete(request: Request, env: Cloudflare.Env): Promise<Response> {
  const key = bearer(request);
  if (!key) return errorResponse(401, "A valid bearer key is required.");

  const parsed = await readSmallJson(request, env);
  if (!parsed.ok) return parsed.response;

  const installId = (parsed.value as { installId?: unknown } | null)?.installId;
  if (typeof installId !== "string" || !GUID_PATTERN.test(installId)) {
    return errorResponse(400, "installId must be a GUID.");
  }

  const state = await checkInstall(env, installId, key);
  if (state === "gone") return errorResponse(410, "This install's data has already been deleted.");
  if (state === "mismatch") return errorResponse(403, "This install's key does not match.");

  const count = await countRequest(env, installId);
  if (count > MAX_REQUESTS_PER_DAY) return errorResponse(429, "Too many requests from this install today.");

  await forgetInstall(env, installId);

  return Response.json({ ok: true });
}

/** Leaves a tombstone first, so any request for the install gives 410 from now on and a report already on its way
 * takes itself back (report.ts, step 11); then deletes the install's objects and rows. A "new" install has nothing
 * to delete but its own just-created row. */
async function forgetInstall(env: Cloudflare.Env, installId: string): Promise<void> {
  await env.DB.prepare("INSERT OR IGNORE INTO tombstones (id, deleted_at) VALUES (?, ?)").bind(installId, Date.now()).run();

  const known = await env.DB.prepare("SELECT r2_key FROM reports WHERE install_id = ?")
    .bind(installId)
    .all<{ r2_key: string }>();
  await deleteInBatches(env, known.results.map((row) => row.r2_key));

  // Anything left under the install's prefix that D1 didn't know about.
  const prefix = `reports/v1/${installId}/`;
  let cursor: string | undefined;
  do {
    const listed = await env.REPORTS.list({ prefix, cursor, limit: 1000 });
    await deleteInBatches(env, listed.objects.map((object) => object.key));
    cursor = listed.truncated ? listed.cursor : undefined;
  } while (cursor);

  await env.DB.batch([
    env.DB.prepare("DELETE FROM reports WHERE install_id = ?").bind(installId),
    env.DB.prepare("DELETE FROM requests WHERE install_id = ?").bind(installId),
    env.DB.prepare("DELETE FROM installs WHERE id = ?").bind(installId),
  ]);
}

async function deleteInBatches(env: Cloudflare.Env, keys: string[]): Promise<void> {
  for (let i = 0; i < keys.length; i += 1000) {
    const batch = keys.slice(i, i + 1000);
    if (batch.length > 0) await env.REPORTS.delete(batch);
  }
}
