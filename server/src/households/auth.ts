import { sha256hex } from "../auth";
import { base64urlDecode, hex, sha256 } from "./encoding";
import { errorResponse } from "./http";

/** Per PC and UTC day, counting every signed request (households design §8). */
export const MAX_REQUESTS_PER_DAY = 200;
/** How far a signed request's X-PL-Time may be from the Worker's clock, either way. */
export const TIME_WINDOW_SECONDS = 300;

export const DEVICE_ID = /^[0-9a-f]{32}$/;
const UNIX_SECONDS = /^[0-9]{1,12}$/;
const SIGNATURE_BYTES = 64;
const MAX_KEY_CHARS = 256;

export interface MemberRow {
  household: string;
  device: string;
  sign_key: string;
  dh_key: string;
  added: number;
  removed: number | null;
}

export interface VerifyOptions {
  /** The Worker's clock in ms; now by default. */
  now?: number;
  /** The household the signer must be a current member of; by default the one in the path. */
  household?: string;
}

/** The first 16 bytes of SHA-256 of the signing key's SPKI, as lower-case hex (HouseholdCrypto.DeviceIdOf). */
export async function deviceIdOf(signSpki: Uint8Array): Promise<string> {
  return hex((await sha256(signSpki)).slice(0, 16));
}

/** What a signed request covers (HouseholdCrypto.RequestToSign): METHOD \n path and query \n unix seconds \n hex SHA-256
 * of the body. */
export function requestToSign(method: string, pathAndQuery: string, unixSeconds: number, bodySha256Hex: string): Uint8Array {
  return new TextEncoder().encode(`${method.toUpperCase()}\n${pathAndQuery}\n${unixSeconds}\n${bodySha256Hex}`);
}

/** An ECDSA P-256 public key from base64url SPKI, or null when it isn't one. */
export async function importSignKey(spki: string): Promise<CryptoKey | null> {
  const bytes = spki.length <= MAX_KEY_CHARS ? base64urlDecode(spki) : null;
  if (!bytes) return null;
  try {
    return await crypto.subtle.importKey("spki", bytes, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  } catch {
    return null;
  }
}

/** Whether `spki` is a base64url ECDH P-256 public key. */
export async function isDhKey(spki: string): Promise<boolean> {
  const bytes = spki.length <= MAX_KEY_CHARS ? base64urlDecode(spki) : null;
  if (!bytes) return false;
  try {
    await crypto.subtle.importKey("spki", bytes, { name: "ECDH", namedCurve: "P-256" }, false, []);
    return true;
  } catch {
    return false;
  }
}

/** ECDSA P-256 over SHA-256, the signature in IEEE P1363 form (r ‖ s), as HouseholdCrypto.SignData makes it. False, not an
 * exception, for a key or signature that can't be read. */
export async function verifySignature(signSpki: string, data: Uint8Array, signature: Uint8Array): Promise<boolean> {
  const key = await importSignKey(signSpki);
  if (!key || signature.byteLength !== SIGNATURE_BYTES) return false;
  try {
    return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, signature, data);
  } catch {
    return false;
  }
}

/** The path and query as sent, like .NET's Uri.PathAndQuery: an empty query keeps its "?", which URL.search drops. */
export function pathAndQuery(request: Request): string {
  const url = new URL(request.url);
  url.hash = "";
  return url.href.slice(url.origin.length);
}

/** The key a device must have signed with, and what to give back when it did; or the refusal. */
type KeyLookup<T> = (device: string) => Promise<{ signKey: string; signer: T } | Response>;

/**
 * The checks every signed request passes (households design §5, plan 0.6), in order: the three X-PL headers are there
 * and well formed (401), the time is within 300 s (401), the device is one `keyFor` knows (its refusal, 403 for a
 * non-member), the signature verifies over RequestToSign (401), the signature hasn't been taken before (401), and the
 * device is within its 200 requests today (429).
 */
async function authenticate<T>(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  now: number,
  keyFor: KeyLookup<T>,
): Promise<T | Response> {
  const device = request.headers.get("X-PL-Device");
  const time = request.headers.get("X-PL-Time");
  const signatureText = request.headers.get("X-PL-Signature");
  const signature = signatureText === null ? null : base64urlDecode(signatureText);
  if (
    device === null || !DEVICE_ID.test(device) ||
    time === null || !UNIX_SECONDS.test(time) ||
    signature === null || signature.byteLength !== SIGNATURE_BYTES
  ) {
    return errorResponse(401, "This request needs the X-PL-Device, X-PL-Time and X-PL-Signature headers.");
  }

  const seconds = Number(time);
  if (Math.abs(now / 1000 - seconds) > TIME_WINDOW_SECONDS) {
    return errorResponse(401, "This request's time is more than 5 minutes off.");
  }

  const found = await keyFor(device);
  if (found instanceof Response) return found;

  const signed = requestToSign(request.method, pathAndQuery(request), seconds, hex(await sha256(body)));
  if (!(await verifySignature(found.signKey, signed, signature))) {
    return errorResponse(401, "This request's signature doesn't match.");
  }

  // r alone marks a signature: ECDSA's s can be flipped to n - s and still verify, so a replay can't dodge this by that.
  const [seen, counted] = await env.DB.batch([
    env.DB.prepare("INSERT INTO seen_signatures (device, r, seen) VALUES (?, ?, ?) ON CONFLICT DO NOTHING RETURNING 1 AS fresh")
      .bind(device, hex(signature.slice(0, 32)), now),
    env.DB.prepare(
      `INSERT INTO device_requests (device, utc_day, count) VALUES (?, ?, 1)
       ON CONFLICT (device, utc_day) DO UPDATE SET count = count + 1
       RETURNING count`,
    ).bind(device, new Date(now).toISOString().slice(0, 10)),
  ]);
  if (seen.results.length === 0) return errorResponse(401, "This request has already been made.");
  if ((counted.results[0] as { count: number }).count > MAX_REQUESTS_PER_DAY) {
    return errorResponse(429, "Too many requests from this PC today.");
  }

  return found.signer;
}

/** The household ID in /v1/households/{hid}/…, or null. */
function householdInPath(request: Request): string | null {
  const parts = new URL(request.url).pathname.split("/");
  return parts[1] === "v1" && parts[2] === "households" && parts[3] ? parts[3] : null;
}

/**
 * A request signed by a current member of the household (the path's, unless `options.household` says): the member's
 * row, or the refusal to send back. A PC that was never a member, or was removed, gets 403.
 */
export async function verifySigned(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  options: VerifyOptions = {},
): Promise<MemberRow | Response> {
  const household = options.household ?? householdInPath(request);
  return authenticate(request, env, body, options.now ?? Date.now(), async (device) => {
    const member = household
      ? await env.DB.prepare(
          "SELECT household, device, sign_key, dh_key, added, removed FROM members WHERE household = ? AND device = ?",
        )
          .bind(household, device)
          .first<MemberRow>()
      : null;
    if (!member || member.removed !== null) return errorResponse(403, "This PC isn't a member of this household.");
    return { signKey: member.sign_key, signer: member };
  });
}

export interface SessionRow {
  token_hash: string;
  account: string;
  device: string;
  sign_key: string;
  dh_key: string;
  created: number;
}

const SESSION_TOKEN = /^[A-Za-z0-9_-]{43}$/;

/** The token from `Authorization: Session <token>`, when it's there and well formed. */
export function sessionToken(request: Request): string | null {
  const header = request.headers.get("Authorization");
  if (!header?.startsWith("Session ")) return null;
  const token = header.slice("Session ".length);
  return SESSION_TOKEN.test(token) ? token : null;
}

/**
 * An account request (households design §7): `Authorization: Session <token>` naming a current session, and the
 * request signed by the PC that session was given to, with the key it signed in with. The session's row, or the
 * refusal to send back (401).
 */
export async function verifySession(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  options: { now?: number } = {},
): Promise<SessionRow | Response> {
  const token = sessionToken(request);
  if (!token) return errorResponse(401, "This request needs a session: sign in first.");
  const tokenHash = await sha256hex(token);
  return authenticate(request, env, body, options.now ?? Date.now(), async (device) => {
    const session = await env.DB.prepare(
      "SELECT token_hash, account, device, sign_key, dh_key, created FROM sessions WHERE token_hash = ?",
    )
      .bind(tokenHash)
      .first<SessionRow>();
    if (!session) return errorResponse(401, "This session has ended: sign in again.");
    if (session.device !== device) return errorResponse(401, "This session is another PC's.");
    return { signKey: session.sign_key, signer: session };
  });
}

/**
 * A request signed by the key it posts, for creating a household or signing in: X-PL-Device must be the ID that key
 * gives. The device ID, or the refusal to send back.
 */
export async function verifySignedByKey(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  signKey: string,
  options: { now?: number } = {},
): Promise<{ device: string } | Response> {
  return authenticate(request, env, body, options.now ?? Date.now(), async (device) => {
    const spki = signKey.length <= MAX_KEY_CHARS ? base64urlDecode(signKey) : null;
    if (!spki || (await deviceIdOf(spki)) !== device) {
      return errorResponse(401, "This request isn't signed by the key it carries.");
    }
    return { signKey, signer: { device } };
  });
}
