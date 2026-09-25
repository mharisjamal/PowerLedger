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
  complete: number;
}

/** `/admin/list`: reports by default, or with `kind=history`, history chunks. */
async function handleList(request: Request, env: Cloudflare.Env): Promise<Response> {
  const kind = new URL(request.url).searchParams.get("kind") ?? "report";
  if (kind === "report") return handleReportList(request, env);
  if (kind === "history") return handleHistoryList(request, env);
  return errorResponse(400, "kind must be report or history.");
}

/** The page size asked for, 1 to 1000; 1000 when it's missing or out of range. */
function pageLimit(url: URL): number {
  const requested = Number(url.searchParams.get("limit"));
  return Number.isInteger(requested) && requested > 0 && requested <= 1000 ? requested : 1000;
}

async function handleReportList(request: Request, env: Cloudflare.Env): Promise<Response> {
  const url = new URL(request.url);
  const from = url.searchParams.get("from");
  const to = url.searchParams.get("to");
  const shared = url.searchParams.get("shared") === "1";
  const after = url.searchParams.get("after");
  const limit = pageLimit(url);

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
            reports.r2_key AS r2Key, reports.complete AS complete
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
      complete: row.complete === 1,
    })),
    next: hasMore && last ? `${last.day}|${last.installId}` : null,
  });
}

interface HistoryListRow {
  installId: string;
  fromMs: number;
  toMs: number;
  country: string;
  receivedAt: number;
  bytes: number;
  r2Key: string;
}

const DAY_MS = 86_400_000;

/** `/admin/list?kind=history`: the history chunks with any hour in the UTC days `from` to `to` (each optional),
 * ordered by first hour then install, paged with `after=<fromMs>|<installId>` as a page's `next` gives it. */
async function handleHistoryList(request: Request, env: Cloudflare.Env): Promise<Response> {
  const url = new URL(request.url);
  const from = url.searchParams.get("from");
  const to = url.searchParams.get("to");
  const shared = url.searchParams.get("shared") === "1";
  const after = url.searchParams.get("after");
  const limit = pageLimit(url);

  const conditions: string[] = [];
  const params: unknown[] = [];
  for (const [day, name] of [[from, "from"], [to, "to"]] as const) {
    if (day && !DAY_PATTERN.test(day)) return errorResponse(400, `${name} must be a day, yyyy-MM-dd.`);
  }
  if (from) {
    conditions.push("histories.to_ms > ?");
    params.push(Date.parse(`${from}T00:00:00Z`));
  }
  if (to) {
    conditions.push("histories.from_ms < ?");
    params.push(Date.parse(`${to}T00:00:00Z`) + DAY_MS);
  }
  if (after) {
    const separator = after.indexOf("|");
    const afterFrom = separator === -1 ? "" : after.slice(0, separator);
    const afterInstallId = separator === -1 ? "" : after.slice(separator + 1);
    if (!/^[0-9]{1,15}$/.test(afterFrom) || !GUID_PATTERN.test(afterInstallId)) {
      return errorResponse(400, "after must be <fromMs>|<installId>, as a page's next gives it.");
    }
    conditions.push("(histories.from_ms > ? OR (histories.from_ms = ? AND histories.install_id > ?))");
    params.push(Number(afterFrom), Number(afterFrom), afterInstallId);
  }
  if (shared) conditions.push("installs.share = 1");
  // As for reports: never what a deleted install left behind.
  conditions.push("histories.install_id NOT IN (SELECT id FROM tombstones)");

  const join = shared ? "JOIN installs ON installs.id = histories.install_id" : "";
  const where = `WHERE ${conditions.join(" AND ")}`;

  const rows = await env.DB.prepare(
    `SELECT histories.install_id AS installId, histories.from_ms AS fromMs, histories.to_ms AS toMs,
            histories.country AS country, histories.received_at AS receivedAt, histories.bytes AS bytes,
            histories.r2_key AS r2Key
     FROM histories ${join} ${where}
     ORDER BY histories.from_ms, histories.install_id
     LIMIT ?`,
  )
    .bind(...params, limit + 1)
    .all<HistoryListRow>();

  const hasMore = rows.results.length > limit;
  const page = hasMore ? rows.results.slice(0, limit) : rows.results;
  const last = page[page.length - 1];

  return Response.json({
    items: page.map((row) => ({
      key: row.r2Key,
      installId: row.installId,
      fromMs: row.fromMs,
      toMs: row.toMs,
      country: row.country,
      receivedAt: row.receivedAt,
      bytes: row.bytes,
    })),
    next: hasMore && last ? `${last.fromMs}|${last.installId}` : null,
  });
}

async function handleObject(request: Request, env: Cloudflare.Env): Promise<Response> {
  const url = new URL(request.url);
  const key = url.searchParams.get("key");
  if (!key || !(key.startsWith("reports/v1/") || key.startsWith("history/v1/"))) {
    return errorResponse(400, "key must be under reports/v1/ or history/v1/.");
  }

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
