/** Small response and body helpers shared by the household routes. */
import { addressOf } from "../address";

export function errorResponse(status: number, message: string): Response {
  return Response.json({ error: message }, { status });
}

export function ok(extra: Record<string, unknown> = {}): Response {
  return Response.json({ ok: true, ...extra });
}

/** The body parsed as a JSON object, or null when it isn't one. */
export function parseObject(body: Uint8Array): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(new TextDecoder().decode(body));
    return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

export function isWholeNumber(value: unknown, max = Number.MAX_SAFE_INTEGER): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= max;
}

/** The 429 to send back when the request's address (an IPv6 one by its /64) is over the per-address limit; else null. */
export async function overAddressLimit(request: Request, env: Cloudflare.Env): Promise<Response | null> {
  const limited = await env.ADDRESS_LIMIT.limit({ key: addressOf(request) });
  return limited.success ? null : errorResponse(429, "Too many requests from this address.");
}
