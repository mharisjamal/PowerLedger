/** The SHA-256 hex digest of a string, lower case. */
export async function sha256hex(input: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(input));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

const BEARER_KEY = /^[A-Za-z0-9_-]{43}$/;

/** The bearer key from `Authorization`, when it's present and a well-formed 43-character
 * base64url key; null otherwise. */
export function bearer(request: Request): string | null {
  const header = request.headers.get("Authorization");
  if (!header?.startsWith("Bearer ")) return null;

  const key = header.slice("Bearer ".length);
  return BEARER_KEY.test(key) ? key : null;
}

/** crypto.subtle.timingSafeEqual only promises constant time for equal-length inputs, so the
 * lengths are checked first. Shared by the install key check here and the admin token check. */
export function timingSafeEqualStrings(a: string, b: string): boolean {
  const aBytes = new TextEncoder().encode(a);
  const bBytes = new TextEncoder().encode(b);
  return aBytes.byteLength === bBytes.byteLength && crypto.subtle.timingSafeEqual(aBytes, bBytes);
}

export type InstallState = "new" | "ok" | "mismatch" | "gone";

/**
 * Trust on first use: an id never seen before is recorded with this key's hash and reported
 * "new"; the same id with the same key is "ok"; with a different key, "mismatch"; a tombstoned
 * id is always "gone". Shared by /v1/report, /v1/consent and /v1/delete.
 */
export async function checkInstall(env: Cloudflare.Env, id: string, key: string): Promise<InstallState> {
  const tombstoned = await env.DB.prepare("SELECT 1 FROM tombstones WHERE id = ?").bind(id).first();
  if (tombstoned) return "gone";

  const hash = await sha256hex(key);
  const row = await env.DB.prepare("SELECT key_hash FROM installs WHERE id = ?")
    .bind(id)
    .first<{ key_hash: string }>();

  if (!row) {
    const now = Date.now();
    await env.DB.prepare(
      `INSERT INTO installs (id, key_hash, first_seen, last_seen, consent_version, diagnostics, usage, power, share, country, app_version)
       VALUES (?, ?, ?, ?, 0, 0, 0, 0, 0, NULL, NULL)`,
    )
      .bind(id, hash, now, now)
      .run();
    return "new";
  }

  return timingSafeEqualStrings(row.key_hash, hash) ? "ok" : "mismatch";
}

/** Which daily count a request takes from: `count` for /v1/report, `history` for /v1/history, `control` for
 * /v1/consent and /v1/delete. Each is its own budget, so a day of hourly reports never blocks a delete. */
export type RequestKind = "count" | "history" | "control";

/** Upserts today's (UTC) count of `kind` for `id` and returns its new total. Shared by /v1/report,
 * /v1/history, /v1/consent and /v1/delete. */
export async function countRequest(env: Cloudflare.Env, id: string, kind: RequestKind = "count"): Promise<number> {
  const utcDay = new Date().toISOString().slice(0, 10);
  // `kind` is one of three fixed column names, never user input.
  const row = await env.DB.prepare(
    `INSERT INTO requests (install_id, utc_day, count, history, control) VALUES (?, ?, ?, ?, ?)
     ON CONFLICT (install_id, utc_day) DO UPDATE SET ${kind} = ${kind} + 1
     RETURNING ${kind} AS n`,
  )
    .bind(id, utcDay, kind === "count" ? 1 : 0, kind === "history" ? 1 : 0, kind === "control" ? 1 : 0)
    .first<{ n: number }>();
  return row!.n;
}
