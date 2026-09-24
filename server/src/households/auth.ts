import { sha256hex } from "../auth";
import { base64urlDecode, hex, sha256 } from "./encoding";
import { errorResponse } from "./http";

/** Per PC and UTC day, counting every signed request. The design's 200 (§8) is below what syncing every 15 minutes takes
 * (a fetch, the member list and, signed in, the join requests, 96 times a day, and a post an hour), so 1000. */
export const MAX_REQUESTS_PER_DAY = 1000;
/** How far a signed request's X-PL-Time may be from the Worker's clock, either way. */
export const TIME_WINDOW_SECONDS = 300;

export const DEVICE_ID = /^[0-9a-f]{32}$/;
const UNIX_SECONDS = /^[0-9]{1,12}$/;
const SIGNATURE_BYTES = 64;
const MAX_KEY_CHARS = 256;

/** Every refusal that could tell a stranger whether a PC is a member says the same: no member row, and a bad signature,
 * both get this one 401. */
export const NOT_SIGNED_BY_A_MEMBER = "This request isn't signed by a member of this household.";
export const REMOVED = "This PC was removed from the household.";

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

/**
 * A P-256 public key from base64url SPKI, only in its canonical encoding: DER with the point uncompressed, as .NET's
 * ExportSubjectPublicKeyInfo and WebCrypto's exportKey give it. WebCrypto also takes a compressed point, but a device ID
 * is a hash of these bytes, so one key must have one encoding: the key is re-exported and must come back byte for byte.
 */
async function importCanonical(spki: string, algorithm: "ECDSA" | "ECDH"): Promise<CryptoKey | null> {
  const bytes = spki.length <= MAX_KEY_CHARS ? base64urlDecode(spki) : null;
  if (!bytes) return null;
  try {
    const usages: string[] = algorithm === "ECDSA" ? ["verify"] : [];
    const key = await crypto.subtle.importKey("spki", bytes, { name: algorithm, namedCurve: "P-256" }, true, usages);
    const exported = new Uint8Array((await crypto.subtle.exportKey("spki", key)) as ArrayBuffer);
    const same = exported.byteLength === bytes.byteLength && exported.every((byte, i) => byte === bytes[i]);
    return same ? key : null;
  } catch {
    return null;
  }
}

/** An ECDSA P-256 public key from canonical base64url SPKI, or null when it isn't one. */
export async function importSignKey(spki: string): Promise<CryptoKey | null> {
  return importCanonical(spki, "ECDSA");
}

/** Whether `spki` is an ECDH P-256 public key in canonical base64url SPKI. */
export async function isDhKey(spki: string): Promise<boolean> {
  return (await importCanonical(spki, "ECDH")) !== null;
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

/** The three X-PL headers of a signed request, read and checked. */
export interface SignedHeaders {
  device: string;
  seconds: number;
  signature: Uint8Array;
}

/** The X-PL headers, well formed and with the time within 300 s of `now` (ms); or the 401 to send back. Only the
 * headers are read, so this can refuse before the body is. */
export function readSignedHeaders(request: Request, now: number): SignedHeaders | Response {
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
  return { device, seconds, signature };
}

/** Whether the request, with this body, is signed by `signKey` (households design §5, plan 0.6). */
async function signatureMatches(request: Request, headers: SignedHeaders, body: Uint8Array, signKey: string): Promise<boolean> {
  const signed = requestToSign(request.method, pathAndQuery(request), headers.seconds, hex(await sha256(body)));
  return verifySignature(signKey, signed, headers.signature);
}

/**
 * Takes a verified request's signature once (401 when it has been taken before) and counts the request against the PC's
 * day (429 past the limit). r alone marks a signature: ECDSA's s can be flipped to n - s and still verify, so a replay
 * can't dodge this by that.
 */
async function takeSignature(env: Cloudflare.Env, headers: SignedHeaders, now: number): Promise<Response | null> {
  const [seen, counted] = await env.DB.batch([
    env.DB.prepare("INSERT INTO seen_signatures (device, r, seen) VALUES (?, ?, ?) ON CONFLICT DO NOTHING RETURNING 1 AS fresh")
      .bind(headers.device, hex(headers.signature.slice(0, 32)), now),
    env.DB.prepare(
      `INSERT INTO device_requests (device, utc_day, count) VALUES (?, ?, 1)
       ON CONFLICT (device, utc_day) DO UPDATE SET count = count + 1
       RETURNING count`,
    ).bind(headers.device, new Date(now).toISOString().slice(0, 10)),
  ]);
  if (seen.results.length === 0) return errorResponse(401, "This request has already been made.");
  if ((counted.results[0] as { count: number }).count > MAX_REQUESTS_PER_DAY) {
    return errorResponse(429, "Too many requests from this PC today.");
  }
  return null;
}

/** The household ID in /v1/households/{hid}/…, or null. */
function householdInPath(request: Request): string | null {
  const parts = new URL(request.url).pathname.split("/");
  return parts[1] === "v1" && parts[2] === "households" && parts[3] ? parts[3] : null;
}

/** A member request that passed checkMember: its headers, the member row it names, and the clock it was checked at. */
export interface MemberCheck {
  headers: SignedHeaders;
  member: MemberRow;
  now: number;
}

/**
 * The first half of a member request, from its headers alone: they're well formed and in time (401), and X-PL-Device
 * has a member row in `household`, current or removed (401, the same as a bad signature, when it has none).
 */
export async function checkMember(
  request: Request,
  env: Cloudflare.Env,
  household: string | null,
  now = Date.now(),
): Promise<MemberCheck | Response> {
  const headers = readSignedHeaders(request, now);
  if (headers instanceof Response) return headers;
  const member = household
    ? await env.DB.prepare(
        "SELECT household, device, sign_key, dh_key, added, removed FROM members WHERE household = ? AND device = ?",
      )
        .bind(household, headers.device)
        .first<MemberRow>()
    : null;
  if (!member) return errorResponse(401, NOT_SIGNED_BY_A_MEMBER);
  return { headers, member, now };
}

/**
 * The second half, with the body: the signature verifies against the member row's key (401, as for no row), and only
 * then is a removed member told so (410); then the signature is taken once and counted. The current member's row.
 */
export async function finishMember(
  request: Request,
  env: Cloudflare.Env,
  check: MemberCheck,
  body: Uint8Array,
): Promise<MemberRow | Response> {
  if (!(await signatureMatches(request, check.headers, body, check.member.sign_key))) {
    return errorResponse(401, NOT_SIGNED_BY_A_MEMBER);
  }
  if (check.member.removed !== null) return errorResponse(410, REMOVED);
  return (await takeSignature(env, check.headers, check.now)) ?? check.member;
}

/**
 * A request signed by a current member of the household (the path's, unless `options.household` says): both halves at
 * once, for a body already read. The member's row, or the refusal to send back.
 */
export async function verifySigned(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  options: VerifyOptions = {},
): Promise<MemberRow | Response> {
  const check = await checkMember(request, env, options.household ?? householdInPath(request), options.now);
  return check instanceof Response ? check : finishMember(request, env, check, body);
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

/** An account request that passed checkSession. */
export interface SessionCheck {
  headers: SignedHeaders;
  session: SessionRow;
  now: number;
}

/**
 * The first half of an account request (households design §7), from its headers alone: `Authorization: Session <token>`
 * names a current session (401), the X-PL headers are well formed and in time (401), and the session is X-PL-Device's
 * (401).
 */
export async function checkSession(request: Request, env: Cloudflare.Env, now = Date.now()): Promise<SessionCheck | Response> {
  const token = sessionToken(request);
  if (!token) return errorResponse(401, "This request needs a session: sign in first.");
  const headers = readSignedHeaders(request, now);
  if (headers instanceof Response) return headers;
  const session = await env.DB.prepare(
    "SELECT token_hash, account, device, sign_key, dh_key, created FROM sessions WHERE token_hash = ?",
  )
    .bind(await sha256hex(token))
    .first<SessionRow>();
  if (!session) return errorResponse(401, "This session has ended: sign in again.");
  if (session.device !== headers.device) return errorResponse(401, "This session is another PC's.");
  return { headers, session, now };
}

/** The second half, with the body: signed with the key the PC signed in with (401), then taken once and counted. */
export async function finishSession(
  request: Request,
  env: Cloudflare.Env,
  check: SessionCheck,
  body: Uint8Array,
): Promise<SessionRow | Response> {
  if (!(await signatureMatches(request, check.headers, body, check.session.sign_key))) {
    return errorResponse(401, "This request's signature doesn't match.");
  }
  return (await takeSignature(env, check.headers, check.now)) ?? check.session;
}

/** An account request, both halves at once, for a body already read. The session's row, or the refusal (401). */
export async function verifySession(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  options: { now?: number } = {},
): Promise<SessionRow | Response> {
  const check = await checkSession(request, env, options.now);
  return check instanceof Response ? check : finishSession(request, env, check, body);
}

/**
 * A request signed by the key it posts, for creating a household or signing in: the key is canonical, X-PL-Device is
 * the ID it gives, and the signature verifies (each 401). The device ID, or the refusal to send back.
 */
export async function verifySignedByKey(
  request: Request,
  env: Cloudflare.Env,
  body: Uint8Array,
  signKey: string,
  options: { now?: number } = {},
): Promise<{ device: string } | Response> {
  const now = options.now ?? Date.now();
  const headers = readSignedHeaders(request, now);
  if (headers instanceof Response) return headers;
  const spki = signKey.length <= MAX_KEY_CHARS ? base64urlDecode(signKey) : null;
  if (!spki || !(await importSignKey(signKey)) || (await deviceIdOf(spki)) !== headers.device) {
    return errorResponse(401, "This request isn't signed by the key it carries.");
  }
  if (!(await signatureMatches(request, headers, body, signKey))) {
    return errorResponse(401, "This request's signature doesn't match.");
  }
  return (await takeSignature(env, headers, now)) ?? { device: headers.device };
}
