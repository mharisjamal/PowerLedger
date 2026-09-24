/** Small response and body helpers shared by the household routes. */

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

/** The request's address, for the per-address rate limit. */
export function addressOf(request: Request): string {
  return request.headers.get("CF-Connecting-IP") ?? "unknown";
}
