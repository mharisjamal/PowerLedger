/**
 * Aero by request (0.10.7): the App asks for the Aero look under a random id it made and keeps, AERO- and 5 Crockford
 * base32 characters, with the PC's display name; the owner approves or revokes it by that id (tools/aero.mjs).
 *
 * POST /v1/aero/request {"id","name"}: records the request once (asking again changes nothing) and answers its state.
 * GET /v1/aero/status?id=: {"state": "none" | "pending" | "approved" | "revoked"}. The id is its own bearer: nothing lists
 * requests without the admin token. Both are under the per-address limit.
 * GET /v1/admin/aero, POST /v1/admin/aero/approve {"id"}, POST /v1/admin/aero/revoke {"id"}: the owner's, behind ADMIN_TOKEN.
 */
import { adminAuthorized } from "./admin";
import { readBounded } from "./body";
import { errorResponse, overAddressLimit, parseObject } from "./households/http";

/** AERO- and 5 Crockford base32 characters (no I, L, O or U). */
export const AERO_ID = /^AERO-[0-9A-HJKMNP-TV-Z]{5}$/;
export const MAX_NAME_CHARS = 64;
const MAX_BODY_BYTES = 1024;
const CONTROL = /[\u0000-\u001F\u007F-\u009F؜​‎‏‪-‮⁠⁦-⁩﻿]/g;

export type AeroState = "none" | "pending" | "approved" | "revoked";

interface AeroRow {
  id: string;
  name: string;
  requested_at: number;
  approved_at: number | null;
  revoked_at: number | null;
}

function stateOf(row: AeroRow | null): AeroState {
  if (!row) return "none";
  if (row.revoked_at !== null) return "revoked";
  if (row.approved_at !== null) return "approved";
  return "pending";
}

async function find(env: Cloudflare.Env, id: string): Promise<AeroRow | null> {
  return env.DB.prepare("SELECT id, name, requested_at, approved_at, revoked_at FROM aero_requests WHERE id = ?")
    .bind(id)
    .first<AeroRow>();
}

async function readJson(request: Request): Promise<Record<string, unknown> | null> {
  const body = await readBounded(request, MAX_BODY_BYTES);
  return body ? parseObject(body) : null;
}

/** POST /v1/aero/request. */
export async function handleAeroRequest(request: Request, env: Cloudflare.Env, now = Date.now()): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;

  const body = await readJson(request);
  if (!body) return errorResponse(400, "The body must be a JSON object.");
  const { id, name } = body;
  if (typeof id !== "string" || !AERO_ID.test(id)) return errorResponse(400, "id must be AERO- and 5 characters.");
  const cleaned = typeof name === "string" ? name.replace(CONTROL, "").trim() : "";
  if (cleaned.length === 0 || cleaned.length > MAX_NAME_CHARS) {
    return errorResponse(400, `name must be 1 to ${MAX_NAME_CHARS} characters.`);
  }

  await env.DB.prepare("INSERT INTO aero_requests (id, name, requested_at) VALUES (?, ?, ?) ON CONFLICT (id) DO NOTHING")
    .bind(id, cleaned, now)
    .run();
  return Response.json({ state: stateOf(await find(env, id)) });
}

/** GET /v1/aero/status?id=. */
export async function handleAeroStatus(request: Request, env: Cloudflare.Env): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;

  const id = new URL(request.url).searchParams.get("id") ?? "";
  if (!AERO_ID.test(id)) return errorResponse(400, "id must be AERO- and 5 characters.");
  return Response.json({ state: stateOf(await find(env, id)) });
}

/** The owner's /v1/admin/aero routes, all requiring ADMIN_TOKEN. */
export async function handleAeroAdmin(request: Request, env: Cloudflare.Env, now = Date.now()): Promise<Response> {
  if (!adminAuthorized(request, env)) return errorResponse(401, "A valid admin token is required.");

  const { pathname } = new URL(request.url);
  if (request.method === "GET" && pathname === "/v1/admin/aero") {
    const rows = await env.DB.prepare(
      "SELECT id, name, requested_at, approved_at, revoked_at FROM aero_requests ORDER BY requested_at DESC, id LIMIT 1000",
    ).all<AeroRow>();
    return Response.json({
      items: rows.results.map((row) => ({
        id: row.id,
        name: row.name,
        state: stateOf(row),
        requestedAt: row.requested_at,
        approvedAt: row.approved_at,
        revokedAt: row.revoked_at,
      })),
    });
  }

  const approve = pathname === "/v1/admin/aero/approve";
  if (request.method === "POST" && (approve || pathname === "/v1/admin/aero/revoke")) {
    const body = await readJson(request);
    const id = body?.id;
    if (typeof id !== "string" || !AERO_ID.test(id)) return errorResponse(400, "id must be AERO- and 5 characters.");
    const sql = approve
      ? "UPDATE aero_requests SET approved_at = ?, revoked_at = NULL WHERE id = ?"
      : "UPDATE aero_requests SET revoked_at = ? WHERE id = ?";
    const result = await env.DB.prepare(sql).bind(now, id).run();
    if (result.meta.changes === 0) return errorResponse(404, "No request with that id.");
    return Response.json({ id, state: stateOf(await find(env, id)) });
  }

  return errorResponse(404, "Not found.");
}
