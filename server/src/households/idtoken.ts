import { base64urlDecode } from "./encoding";

/** Sign-in's two providers (households design §7). */
export type Provider = "microsoft" | "google";

export const JWKS_URLS: Record<Provider, string> = {
  microsoft: "https://login.microsoftonline.com/common/discovery/v2.0/keys",
  google: "https://www.googleapis.com/oauth2/v3/certs",
};

const JWKS_TTL_MS = 60 * 60 * 1000;
/** A key ID the cache hasn't seen sends it back to the provider, which may have just rolled a key in, at most this often. */
const JWKS_RETRY_MS = 60 * 1000;
/** Allowance for clocks, on the token's expiry and issue times. */
const CLOCK_SKEW_SECONDS = 300;
const TENANT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const MAX_TOKEN_CHARS = 16 * 1024;

/** One key of a provider's JWKS, as published; only RSA keys are used. */
export interface Jwk {
  kty: string;
  kid?: string;
  n?: string;
  e?: string;
  alg?: string;
  use?: string;
}

export type JwksFetcher = (url: string) => Promise<Jwk[]>;

export async function fetchJwks(url: string): Promise<Jwk[]> {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url} gave ${response.status}.`);
  const body = (await response.json()) as { keys?: unknown };
  if (!Array.isArray(body.keys)) throw new Error(`${url} gave no keys.`);
  return body.keys as Jwk[];
}

/** Each provider's keys, fetched when first needed and kept an hour. A fetch that fails throws. */
export class JwksCache {
  private readonly entries = new Map<string, { keys: Jwk[]; fetchedAt: number }>();

  constructor(private readonly fetcher: JwksFetcher = fetchJwks) {}

  async key(url: string, kid: string, now: number): Promise<Jwk | null> {
    let entry = this.entries.get(url);
    if (!entry || now - entry.fetchedAt >= JWKS_TTL_MS) entry = await this.refresh(url, now);

    let key = entry.keys.find((candidate) => candidate.kid === kid);
    if (!key && now - entry.fetchedAt >= JWKS_RETRY_MS) {
      entry = await this.refresh(url, now);
      key = entry.keys.find((candidate) => candidate.kid === kid);
    }
    return key ?? null;
  }

  private async refresh(url: string, now: number): Promise<{ keys: Jwk[]; fetchedAt: number }> {
    const entry = { keys: await this.fetcher(url), fetchedAt: now };
    this.entries.set(url, entry);
    return entry;
  }
}

export type TokenCheck = { ok: true; subject: string } | { ok: false; status: 401 | 503; message: string };

function refuse(message: string): TokenCheck {
  return { ok: false, status: 401, message };
}

function decodeJson(part: string): Record<string, unknown> | null {
  const bytes = base64urlDecode(part);
  if (!bytes) return null;
  try {
    const value: unknown = JSON.parse(new TextDecoder().decode(bytes));
    return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function issuerIsRight(provider: Provider, claims: Record<string, unknown>): boolean {
  if (provider === "google") return claims.iss === "https://accounts.google.com" || claims.iss === "accounts.google.com";
  // Microsoft's common endpoint serves every tenant: the issuer names the token's own, from its tid claim.
  return typeof claims.tid === "string" && TENANT_ID.test(claims.tid) &&
    claims.iss === `https://login.microsoftonline.com/${claims.tid}/v2.0`;
}

function audienceIsRight(claims: Record<string, unknown>, clientId: string): boolean {
  if (claims.aud === clientId) return true;
  // Several audiences are only taken when this app is the one the token was issued to.
  return Array.isArray(claims.aud) && claims.aud.includes(clientId) && claims.azp === clientId;
}

/**
 * Checks an OpenID Connect ID token (households design §7): RS256 by a key in the provider's JWKS, the provider's
 * issuer, this app's client ID as audience, not expired nor issued in the future (5 minutes' allowance), and the nonce
 * the sign-in was started with. The subject is all that's kept.
 */
export async function checkIdToken(
  token: string,
  provider: Provider,
  clientId: string,
  nonce: string,
  jwks: JwksCache,
  now: number,
): Promise<TokenCheck> {
  const parts = token.length <= MAX_TOKEN_CHARS ? token.split(".") : [];
  if (parts.length !== 3) return refuse("The ID token isn't a JWT.");
  const header = decodeJson(parts[0]);
  const claims = decodeJson(parts[1]);
  const signature = base64urlDecode(parts[2]);
  if (!header || !claims || !signature) return refuse("The ID token isn't a JWT.");
  if (header.alg !== "RS256" || typeof header.kid !== "string") return refuse("The ID token must be signed RS256.");

  let jwk: Jwk | null;
  try {
    jwk = await jwks.key(JWKS_URLS[provider], header.kid, now);
  } catch {
    return { ok: false, status: 503, message: "The sign-in provider's keys can't be fetched right now." };
  }
  if (!jwk || jwk.kty !== "RSA" || !jwk.n || !jwk.e) return refuse("The ID token's key isn't one the provider publishes.");

  let verified = false;
  try {
    const key = await crypto.subtle.importKey(
      "jwk",
      { kty: "RSA", n: jwk.n, e: jwk.e },
      { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
      false,
      ["verify"],
    );
    verified = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, signature, new TextEncoder().encode(`${parts[0]}.${parts[1]}`));
  } catch {
    verified = false;
  }
  if (!verified) return refuse("The ID token's signature doesn't match.");

  const seconds = now / 1000;
  if (!issuerIsRight(provider, claims)) return refuse("The ID token is from another issuer.");
  if (!audienceIsRight(claims, clientId)) return refuse("The ID token is for another app.");
  if (typeof claims.exp !== "number" || claims.exp + CLOCK_SKEW_SECONDS < seconds) return refuse("The ID token has expired.");
  if (typeof claims.iat === "number" && claims.iat - CLOCK_SKEW_SECONDS > seconds) return refuse("The ID token is from the future.");
  if (claims.nonce !== nonce) return refuse("The ID token's nonce doesn't match.");
  if (typeof claims.sub !== "string" || claims.sub.length === 0 || claims.sub.length > 255) {
    return refuse("The ID token has no subject.");
  }

  return { ok: true, subject: claims.sub };
}
