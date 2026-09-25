import { addressOf } from "./address";
import { bearer } from "./auth";
import { readBounded } from "./body";
import { gunzipBounded } from "./gzip";
import { checkVersionHeader } from "./version";

const MAX_BODY_BYTES = 1_048_576;
const MAX_UNPACKED_BYTES = 8 * 1024 * 1024;

export function errorResponse(status: number, message: string): Response {
  return Response.json({ error: message }, { status });
}

export type Upload =
  | { ok: true; key: string; body: Uint8Array; value: unknown }
  | { ok: false; response: Response };

/**
 * The checks /v1/report and /v1/history share, in order, before either looks at what was sent: a well-formed bearer
 * key; an X-PowerLedger-Version header, when there is one, at or above the minimum (426 below it, version.ts); the
 * per-address rate limit (an IPv6 address by its /64); gzip, at most 1 MB as sent, counted while reading so an
 * oversized body is never held whole; bounded decompression (8 MB); then JSON. `body` is the bytes as sent.
 */
export async function readUpload(request: Request, env: Cloudflare.Env): Promise<Upload> {
  const fail = (status: number, message: string): Upload => ({ ok: false, response: errorResponse(status, message) });

  const key = bearer(request);
  if (!key) return fail(401, "A valid bearer key is required.");

  const tooOld = checkVersionHeader(request, env);
  if (tooOld) return { ok: false, response: tooOld };

  const limited = await env.ADDRESS_LIMIT.limit({ key: addressOf(request) });
  if (!limited.success) return fail(429, "Too many requests from this address.");

  if (request.headers.get("Content-Encoding") !== "gzip") {
    return fail(413, "The body must be sent gzip-encoded.");
  }
  const body = await readBounded(request, MAX_BODY_BYTES);
  if (body === null) return fail(413, "The body is larger than 1 MB.");

  let unpacked: Uint8Array | null;
  try {
    unpacked = await gunzipBounded(body, MAX_UNPACKED_BYTES);
  } catch {
    return fail(400, "The body is not valid gzip.");
  }
  if (unpacked === null) return fail(413, "The unpacked body is larger than 8 MB.");

  try {
    return { ok: true, key, body, value: JSON.parse(new TextDecoder().decode(unpacked)) };
  } catch {
    return fail(400, "The body is not valid JSON.");
  }
}
