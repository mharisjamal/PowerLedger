import { sha256hex } from "../auth";
import { verifySignedByKey } from "./auth";
import { base64urlEncode } from "./encoding";
import { readKeys, readSmall } from "./households";
import { addressOf, errorResponse, parseObject } from "./http";
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
 * POST /v1/auth/signin: {"provider","idToken","nonce","sign","dh"}, signed by the PC with that signing key. Checks the ID
 * token, keeps the account as provider and subject only, and gives the PC a new session: 32 random bytes, kept hashed.
 * Answers {"session","householdId","hasRecovery"}, the household being the one the account is linked to, or null.
 */
export async function handleSignin(request: Request, env: Cloudflare.Env, deps: SigninDeps = defaultDeps): Promise<Response> {
  const limited = await env.ADDRESS_LIMIT.limit({ key: addressOf(request) });
  if (!limited.success) return errorResponse(429, "Too many requests from this address.");

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
  const check = await checkIdToken(posted.idToken, provider, clientId, posted.nonce, deps.jwks, now);
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
    householdId: (link.results[0] as { household: string } | undefined)?.household ?? null,
    hasRecovery: recovery.results.length > 0,
  });
}
