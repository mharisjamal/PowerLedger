/**
 * The minimum app version (Plan Q spec §3). MIN_APP_VERSION in wrangler.toml's [vars] is the oldest version whose
 * uploads are taken: /v1/report and /v1/history answer anything older with 426, storing nothing, and
 * GET /v1/app-policy tells the service the minimum so it can update itself first. Consent and delete are never
 * refused for age, so an old app can always take its data back.
 */

export type Version = readonly [number, number, number];

export const VERSION_HEADER = "X-PowerLedger-Version";

const VERSION = /^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(?:[+-][0-9A-Za-z.+-]*)?$/;

/** X.Y.Z as three numbers, a build or pre-release suffix (`+sha`, `-beta`) ignored; null for anything else. */
export function parseVersion(text: string): Version | null {
  const match = VERSION.exec(text);
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
}

/** Below zero when `a` is older than `b`, zero when the same, above zero when newer. */
export function compareVersions(a: Version, b: Version): number {
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
}

/** MIN_APP_VERSION as X.Y.Z; 0.0.0 (every version taken) when it's empty or not a version, so a slip in the
 * config never refuses everyone. */
export function minVersion(env: Cloudflare.Env): string {
  const parsed = parseVersion(env.MIN_APP_VERSION ?? "");
  return parsed ? parsed.join(".") : "0.0.0";
}

/** The 426 an upload from a version below the minimum gets. */
export function updateRequired(env: Cloudflare.Env): Response {
  return Response.json({ error: "update required", minVersion: minVersion(env) }, { status: 426 });
}

/** True when `version` (already a valid X.Y.Z) is below the minimum. */
export function belowMinimum(version: string, env: Cloudflare.Env): boolean {
  const parsed = parseVersion(version);
  return parsed !== null && compareVersions(parsed, parseVersion(minVersion(env))!) < 0;
}

/** The answer for a request whose X-PowerLedger-Version header is below the minimum (426) or isn't a version (400);
 * null when it's fine or missing (the body's `app` is checked either way). */
export function checkVersionHeader(request: Request, env: Cloudflare.Env): Response | null {
  const header = request.headers.get(VERSION_HEADER);
  if (header === null) return null;
  if (!parseVersion(header)) return Response.json({ error: `${VERSION_HEADER} must be X.Y.Z.` }, { status: 400 });
  return belowMinimum(header, env) ? updateRequired(env) : null;
}

/** GET /v1/app-policy: `{"minVersion"}`, with no auth, cacheable for 5 minutes. */
export function handleAppPolicy(env: Cloudflare.Env): Response {
  return Response.json({ minVersion: minVersion(env) }, { headers: { "Cache-Control": "public, max-age=300" } });
}
