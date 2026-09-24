import { sha256hex } from "../auth";
import { readSignedHeaders, verifySignedByKey } from "./auth";
import { base64urlEncode, sha256 } from "./encoding";
import { readKeys, readSmall } from "./households";
import { errorResponse, overAddressLimit, parseObject } from "./http";
import { checkIdToken, JwksCache, type Provider } from "./idtoken";

const MAX_NONCE_CHARS = 256;

export interface SigninDeps {
  jwks: JwksCache;
}

/** One cache per isolate: each provider's keys are fetched at most hourly (households design §7). */
const defaultDeps: SigninDeps = { jwks: new JwksCache() };

function clientIdFor(env: Cloudflare.Env, provider: Provider): string | undefined {
  return provider === "microsoft" ? env.MS_CLIENT_ID : env.GOOGLE_CLIENT_ID;
}

/**
 * The nonce an ID token must carry to sign in the PC `device` (its verified X-PL-Device): base64url, unpadded, of SHA-256
 * of the UTF-8 of "<device ID>:<salt>", the salt being what the PC posts as "nonce". The App asks the provider for this
 * nonce, so a token it got can only sign in the PC that asked for it: another PC presenting it, even with the same salt,
 * gives another expected nonce and is refused.
 */
export async function boundNonce(device: string, salt: string): Promise<string> {
  return base64urlEncode(await sha256(new TextEncoder().encode(`${device}:${salt}`)));
}

/**
 * POST /v1/auth/signin: {"provider","idToken","nonce","sign","dh"}, signed by the PC with that signing key; "nonce" is a
 * salt, and the ID token's nonce claim must be boundNonce(the signing PC's device ID, salt). Checks the ID token, keeps
 * the account as provider and subject only, and gives the PC a new session: 32 random bytes, kept hashed. Answers
 * {"session","account","householdId","hasRecovery"}: the account's opaque ID (what join requests show), and the
 * household it's linked to, or null.
 */
export async function handleSignin(request: Request, env: Cloudflare.Env, deps: SigninDeps = defaultDeps): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;
  const headers = readSignedHeaders(request, Date.now());
  if (headers instanceof Response) return headers;

  const body = await readSmall(request);
  if (body instanceof Response) return body;

  const posted = parseObject(body);
  const provider = posted?.provider;
  if (provider !== "microsoft" && provider !== "google") return errorResponse(400, 'provider must be "microsoft" or "google".');
  if (typeof posted?.idToken !== "string" || typeof posted.nonce !== "string" ||
    posted.nonce.length === 0 || posted.nonce.length > MAX_NONCE_CHARS) {
    return errorResponse(400, "idToken and nonce must be strings.");
  }
  const keys = await readKeys(posted);
  if (keys instanceof Response) return keys;

  const signer = await verifySignedByKey(request, env, body, keys.sign);
  if (signer instanceof Response) return signer;

  const clientId = clientIdFor(env, provider);
  if (!clientId) return errorResponse(503, `Signing in with ${provider === "microsoft" ? "Microsoft" : "Google"} isn't set up.`);

  const now = Date.now();
  const expectedNonce = await boundNonce(signer.device, posted.nonce);
  const check = await checkIdToken(posted.idToken, provider, clientId, expectedNonce, deps.jwks, now);
  if (!check.ok) return errorResponse(check.status, check.message);

  const account = await env.DB.prepare(
    `INSERT INTO accounts (id, provider, subject, created) VALUES (?, ?, ?, ?)
     ON CONFLICT (provider, subject) DO UPDATE SET subject = excluded.subject
     RETURNING id`,
  )
    .bind(crypto.randomUUID(), provider, check.subject, now)
    .first<{ id: string }>();

  const session = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
  const [, , link, recovery] = await env.DB.batch([
    env.DB.prepare("DELETE FROM sessions WHERE device = ?").bind(signer.device),
    env.DB.prepare(
      "INSERT INTO sessions (token_hash, account, device, sign_key, dh_key, created) VALUES (?, ?, ?, ?, ?, ?)",
    ).bind(await sha256hex(session), account!.id, signer.device, keys.sign, keys.dh, now),
    env.DB.prepare("SELECT household FROM account_households WHERE account = ?").bind(account!.id),
    env.DB.prepare("SELECT 1 AS present FROM recovery WHERE account = ?").bind(account!.id),
  ]);

  return Response.json({
    session,
    account: account!.id,
    householdId: (link.results[0] as { household: string } | undefined)?.household ?? null,
    hasRecovery: recovery.results.length > 0,
  });
}
