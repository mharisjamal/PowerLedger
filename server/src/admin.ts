import { timingSafeEqualStrings } from "./auth";
import { GUID_PATTERN } from "./schema";
import { getBody } from "./store";

const DAY_PATTERN = /^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])$/;

function errorResponse(status: number, message: string): Response {
  return Response.json({ error: message }, { status });
}

/** True only when ADMIN_TOKEN is set and the request's bearer token matches it, timing-safe. An
 * empty or missing ADMIN_TOKEN refuses every request, even one with an empty token. */
export function adminAuthorized(request: Request, env: Cloudflare.Env): boolean {
  if (!env.ADMIN_TOKEN) return false;

  const header = request.headers.get("Authorization");
  if (!header?.startsWith("Bearer ")) return false;

  return timingSafeEqualStrings(env.ADMIN_TOKEN, header.slice("Bearer ".length));
}

interface StatsRow {
  n: number;
}
interface SwitchesRow {
  diagnostics: number | null;
  usage: number | null;
  power: number | null;
  share: number | null;
}
interface DayRow {
  day: string;
  reports: number;
  bytes: number;
}

async function handleStats(env: Cloudflare.Env): Promise<Response> {
  const installs = await env.DB.prepare("SELECT COUNT(*) AS n FROM installs").first<StatsRow>();
  const switches = await env.DB.prepare(
    "SELECT SUM(diagnostics) AS diagnostics, SUM(usage) AS usage, SUM(power) AS power, SUM(share) AS share FROM installs",
  ).first<SwitchesRow>();
  const days = await env.DB.prepare(
    "SELECT day, COUNT(*) AS reports, SUM(bytes) AS bytes FROM reports GROUP BY day ORDER BY day DESC LIMIT 30",
  ).all<DayRow>();

  return Response.json({
    installs: installs?.n ?? 0,
    switches: {
      diagnostics: switches?.diagnostics ?? 0,
      usage: switches?.usage ?? 0,
      power: switches?.power ?? 0,
      share: switches?.share ?? 0,
    },
    days: days.results,
  });
}

interface ListRow {
  installId: string;
  day: string;
  country: string;
  receivedAt: number;
  bytes: number;
  sections: string;
  r2Key: string;
}

async function handleList(request: Request, env: Cloudflare.Env): Promise<Response> {
  const url = new URL(request.url);
  const from = url.searchParams.get("from");
  const to = url.searchParams.get("to");
  const shared = url.searchParams.get("shared") === "1";
  const after = url.searchParams.get("after");
  const requestedLimit = Number(url.searchParams.get("limit"));
  const limit = Number.isInteger(requestedLimit) && requestedLimit > 0 && requestedLimit <= 1000 ? requestedLimit : 1000;

  const conditions: string[] = [];
  const params: unknown[] = [];
  if (from) {
    conditions.push("reports.day >= ?");
    params.push(from);
  }
  if (to) {
    conditions.push("reports.day <= ?");
    params.push(to);
  }
  if (after) {
    const separator = after.indexOf("|");
    const afterDay = separator === -1 ? "" : after.slice(0, separator);
    const afterInstallId = separator === -1 ? "" : after.slice(separator + 1);
    if (!DAY_PATTERN.test(afterDay) || !GUID_PATTERN.test(afterInstallId)) {
      return errorResponse(400, "after must be <day>|<installId>, as a page's next gives it.");
    }
    conditions.push("(reports.day > ? OR (reports.day = ? AND reports.install_id > ?))");
    params.push(afterDay, afterDay, afterInstallId);
  }
  if (shared) conditions.push("installs.share = 1");
  // A report stored while its install was being deleted is taken back at once (report.ts); this keeps one that
  // hasn't been yet out of every listing, shared or not.
  conditions.push("reports.install_id NOT IN (SELECT id FROM tombstones)");

  const join = shared ? "JOIN installs ON installs.id = reports.install_id" : "";
  const where = conditions.length > 0 ? `WHERE ${conditions.join(" AND ")}` : "";

  const rows = await env.DB.prepare(
    `SELECT reports.install_id AS installId, reports.day AS day, reports.country AS country,
            reports.received_at AS receivedAt, reports.bytes AS bytes, reports.sections AS sections,
            reports.r2_key AS r2Key
     FROM reports ${join} ${where}
     ORDER BY reports.day, reports.install_id
     LIMIT ?`,
  )
    .bind(...params, limit + 1)
    .all<ListRow>();

  const hasMore = rows.results.length > limit;
  const page = hasMore ? rows.results.slice(0, limit) : rows.results;
  const last = page[page.length - 1];

  return Response.json({
    items: page.map((row) => ({
      key: row.r2Key,
      installId: row.installId,
      day: row.day,
      country: row.country,
      receivedAt: row.receivedAt,
      bytes: row.bytes,
      sections: row.sections,
    })),
    next: hasMore && last ? `${last.day}|${last.installId}` : null,
  });
}

async function handleObject(request: Request, env: Cloudflare.Env): Promise<Response> {
  const url = new URL(request.url);
  const key = url.searchParams.get("key");
  if (!key || !key.startsWith("reports/v1/")) return errorResponse(400, "key must be under reports/v1/.");

  const bytes = await getBody(env, key);
  if (!bytes) return errorResponse(404, "No object with that key.");

  return new Response(bytes, { headers: { "Content-Type": "application/gzip" } });
}

/** Routes the owner-only /admin/* endpoints, all requiring ADMIN_TOKEN. */
export async function handleAdmin(request: Request, env: Cloudflare.Env): Promise<Response> {
  if (!adminAuthorized(request, env)) return errorResponse(401, "A valid admin token is required.");

  const { pathname } = new URL(request.url);
  if (request.method === "GET" && pathname === "/admin/stats") return handleStats(env);
  if (request.method === "GET" && pathname === "/admin/list") return handleList(request, env);
  if (request.method === "GET" && pathname === "/admin/object") return handleObject(request, env);

  return errorResponse(404, "Not found.");
}
