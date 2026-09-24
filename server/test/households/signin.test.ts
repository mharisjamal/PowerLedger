import { env, SELF } from "cloudflare:test";
import { beforeAll, describe, expect, it } from "vitest";
import { sha256hex } from "../../src/auth";
import { base64urlEncode } from "../../src/households/encoding";
import { type Jwk, JwksCache, JWKS_URLS } from "../../src/households/idtoken";
import { handleSignin } from "../../src/households/signin";
import { newDevice, randomAddress, randomHouseholdId, signedRequest, signHeaders, type TestDevice } from "./support";

const MS_CLIENT = "11111111-2222-3333-4444-555555555555";
const GOOGLE_CLIENT = "test-client.apps.googleusercontent.com";
const TENANT = "9188040d-6c67-4c5b-b112-36a304b66dad";
const testEnv = { ...env, MS_CLIENT_ID: MS_CLIENT, GOOGLE_CLIENT_ID: GOOGLE_CLIENT } as Cloudflare.Env;

let providerKey: CryptoKeyPair;
let otherKey: CryptoKeyPair;
let jwks: Jwk[];

async function rsaKey(): Promise<CryptoKeyPair> {
  return (await crypto.subtle.generateKey(
    { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
    true,
    ["sign", "verify"],
  )) as CryptoKeyPair;
}

beforeAll(async () => {
  providerKey = await rsaKey();
  otherKey = await rsaKey();
  const exported = (await crypto.subtle.exportKey("jwk", providerKey.publicKey)) as JsonWebKey;
  jwks = [{ kty: "RSA", kid: "test-key", use: "sig", alg: "RS256", n: exported.n!, e: exported.e! }];
});

/** A provider serving the test JWKS, counting how often it's asked. */
function testProvider(): { cache: JwksCache; fetched: string[] } {
  const fetched: string[] = [];
  const cache = new JwksCache(async (url) => {
    fetched.push(url);
    return jwks;
  });
  return { cache, fetched };
}

function encodeJson(value: unknown): string {
  return base64urlEncode(new TextEncoder().encode(JSON.stringify(value)));
}

async function idToken(
  claims: Record<string, unknown>,
  options: { kid?: string; alg?: string; key?: CryptoKey } = {},
): Promise<string> {
  const head = encodeJson({ alg: options.alg ?? "RS256", kid: options.kid ?? "test-key", typ: "JWT" });
  const body = encodeJson(claims);
  const signature = await crypto.subtle.sign(
    "RSASSA-PKCS1-v1_5",
    options.key ?? providerKey.privateKey,
    new TextEncoder().encode(`${head}.${body}`),
  );
  return `${head}.${body}.${base64urlEncode(new Uint8Array(signature))}`;
}

/** The nonce a sign-in by `device` asks the provider for: base64url(SHA-256(UTF-8("<device ID>:<salt>"))), the salt being
 * what the PC posts as "nonce". Worked out here on its own, so a change to the Worker's side shows. */
async function boundNonce(device: TestDevice, salt = "the-salt"): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(`${device.id}:${salt}`));
  return base64urlEncode(new Uint8Array(digest));
}

/** A Microsoft ID token's claims, its nonce bound to `device`. */
async function microsoftClaims(device: TestDevice, overrides: Record<string, unknown> = {}): Promise<Record<string, unknown>> {
  const now = Math.floor(Date.now() / 1000);
  return {
    iss: `https://login.microsoftonline.com/${TENANT}/v2.0`,
    aud: MS_CLIENT,
    tid: TENANT,
    sub: `ms-${crypto.randomUUID()}`,
    nonce: await boundNonce(device),
    iat: now - 10,
    exp: now + 3600,
    email: "someone@example.com",
    ...overrides,
  };
}

/** A Google ID token's claims, its nonce bound to `device`. */
async function googleClaims(device: TestDevice, overrides: Record<string, unknown> = {}): Promise<Record<string, unknown>> {
  const now = Math.floor(Date.now() / 1000);
  return {
    iss: "https://accounts.google.com",
    aud: GOOGLE_CLIENT,
    sub: `g-${Math.floor(Math.random() * 1e15)}`,
    nonce: await boundNonce(device),
    iat: now - 10,
    exp: now + 3600,
    ...overrides,
  };
}

/** A sign-in request signed by `device`, posting `salt` as its "nonce". */
async function signinRequest(device: TestDevice, provider: string, token: string, salt = "the-salt"): Promise<Request> {
  const body = JSON.stringify({ provider, idToken: token, nonce: salt, sign: device.sign, dh: device.dh });
  const headers = { ...(await signHeaders(device, "POST", "/v1/auth/signin", body)), "CF-Connecting-IP": randomAddress() };
  return new Request("https://example.com/v1/auth/signin", { method: "POST", headers, body });
}

interface SigninReply {
  session: string;
  householdId: string | null;
  hasRecovery: boolean;
}

describe("POST /v1/auth/signin", () => {
  it("signs a PC in with a Microsoft ID token: an account, a session, no household yet", async () => {
    const pc = await newDevice();
    const claims = await microsoftClaims(pc);
    const { cache, fetched } = testProvider();

    const response = await handleSignin(await signinRequest(pc, "microsoft", await idToken(claims)), testEnv, { jwks: cache });
    expect(response.status).toBe(200);
    const reply = (await response.json()) as SigninReply;
    expect(reply).toEqual({ session: expect.stringMatching(/^[A-Za-z0-9_-]{43}$/), householdId: null, hasRecovery: false });
    expect(fetched).toEqual([JWKS_URLS.microsoft]);

    const account = await env.DB.prepare("SELECT id, provider, subject FROM accounts WHERE subject = ?")
      .bind(claims.sub)
      .first<{ id: string; provider: string; subject: string }>();
    expect(account).toMatchObject({ provider: "microsoft", subject: claims.sub });

    const session = await env.DB.prepare("SELECT account, device, sign_key, dh_key FROM sessions WHERE token_hash = ?")
      .bind(await sha256hex(reply.session))
      .first();
    expect(session).toEqual({ account: account!.id, device: pc.id, sign_key: pc.sign, dh_key: pc.dh });
  });

  it("signs a PC in with a Google ID token, keeping one account per subject", async () => {
    const sub = `g-${crypto.randomUUID()}`;
    const { cache, fetched } = testProvider();

    for (const pc of [await newDevice(), await newDevice()]) {
      const token = await idToken(await googleClaims(pc, { sub }));
      const response = await handleSignin(await signinRequest(pc, "google", token), testEnv, { jwks: cache });
      expect(response.status).toBe(200);
    }

    const accounts = await env.DB.prepare("SELECT id FROM accounts WHERE provider = 'google' AND subject = ?").bind(sub).all();
    expect(accounts.results).toHaveLength(1);
    const sessions = await env.DB.prepare("SELECT device FROM sessions WHERE account = ?").bind(accounts.results[0].id).all();
    expect(sessions.results).toHaveLength(2);
    expect(fetched).toEqual([JWKS_URLS.google]);
  });

  it("gives the account's household and whether it has a recovery envelope", async () => {
    const sub = `g-${crypto.randomUUID()}`;
    const { cache } = testProvider();
    const signIn = async (): Promise<Response> => {
      const pc = await newDevice();
      return handleSignin(await signinRequest(pc, "google", await idToken(await googleClaims(pc, { sub }))), testEnv, { jwks: cache });
    };
    const first = await signIn();
    const account = await env.DB.prepare("SELECT id FROM accounts WHERE subject = ?").bind(sub).first<{ id: string }>();
    expect(first.status).toBe(200);

    const hid = randomHouseholdId();
    await env.DB.batch([
      env.DB.prepare("INSERT INTO account_households (account, household, linked) VALUES (?, ?, 1)").bind(account!.id, hid),
      env.DB.prepare("INSERT INTO recovery (account, body, verifier, epoch, updated) VALUES (?, 'b', 'v', 1, 1)").bind(account!.id),
    ]);

    const again = await signIn();
    expect(await again.json()).toMatchObject({ householdId: hid, hasRecovery: true });
  });

  it("keeps one session per PC: signing in again replaces the last", async () => {
    const pc = await newDevice();
    const { cache } = testProvider();

    const signIn = async (): Promise<SigninReply> => {
      const token = await idToken(await googleClaims(pc));
      return (await (await handleSignin(await signinRequest(pc, "google", token), testEnv, { jwks: cache })).json()) as SigninReply;
    };
    const one = await signIn();
    const two = await signIn();

    const sessions = await env.DB.prepare("SELECT token_hash FROM sessions WHERE device = ?").bind(pc.id).all<{ token_hash: string }>();
    expect(sessions.results.map((row) => row.token_hash)).toEqual([await sha256hex(two.session)]);
    expect(one.session).not.toBe(two.session);
  });

  it("refuses an ID token that isn't right, with 401", async () => {
    const now = Math.floor(Date.now() / 1000);
    type TokenFor = (pc: TestDevice) => Promise<string>;
    const cases: [string, string, TokenFor, string][] = [
      ["another audience", "microsoft", async (pc) => idToken(await microsoftClaims(pc, { aud: "someone-else" })), "another app"],
      ["another tenant's issuer", "microsoft", async (pc) => idToken(await microsoftClaims(pc, { iss: "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0" })), "another issuer"],
      ["no tenant", "microsoft", async (pc) => idToken(await microsoftClaims(pc, { tid: undefined })), "another issuer"],
      ["Google's audience at Microsoft", "microsoft", async (pc) => idToken(await microsoftClaims(pc, { aud: GOOGLE_CLIENT })), "another app"],
      ["another issuer", "google", async (pc) => idToken(await googleClaims(pc, { iss: "https://evil.example.com" })), "another issuer"],
      ["expired", "google", async (pc) => idToken(await googleClaims(pc, { exp: now - 600 })), "expired"],
      ["issued in the future", "google", async (pc) => idToken(await googleClaims(pc, { iat: now + 3600 })), "future"],
      ["the salt as the nonce, unbound", "google", async (pc) => idToken(await googleClaims(pc, { nonce: "the-salt" })), "nonce"],
      ["no subject", "google", async (pc) => idToken(await googleClaims(pc, { sub: "" })), "subject"],
      ["signed by another key", "google", async (pc) => idToken(await googleClaims(pc), { key: otherKey.privateKey }), "signature"],
      ["an unknown key", "google", async (pc) => idToken(await googleClaims(pc), { kid: "unknown" }), "key"],
      ["not RS256", "google", async (pc) => idToken(await googleClaims(pc), { alg: "HS256" }), "RS256"],
      ["not a JWT", "google", async () => "not.a.jwt", "JWT"],
    ];

    for (const [name, provider, tokenFor, reason] of cases) {
      const { cache } = testProvider();
      const pc = await newDevice();
      const response = await handleSignin(await signinRequest(pc, provider, await tokenFor(pc)), testEnv, { jwks: cache });
      expect(response.status, name).toBe(401);
      expect(((await response.json()) as { error: string }).error, name).toContain(reason);
    }
  });

  it("refuses an ID token asked for by another PC, even with the same salt", async () => {
    const asker = await newDevice();
    const presenter = await newDevice();
    const token = await idToken(await googleClaims(asker));
    const { cache } = testProvider();

    const stolen = await handleSignin(await signinRequest(presenter, "google", token), testEnv, { jwks: cache });
    expect(stolen.status).toBe(401);
    expect(((await stolen.json()) as { error: string }).error).toBe("The ID token wasn't asked for by this PC: its nonce doesn't match.");

    const otherSalt = await handleSignin(await signinRequest(asker, "google", token, "another-salt"), testEnv, { jwks: cache });
    expect(otherSalt.status).toBe(401);

    const own = await handleSignin(await signinRequest(asker, "google", token), testEnv, { jwks: cache });
    expect(own.status).toBe(200);
  });

  it("refuses a request the PC didn't sign with the key it posts", async () => {
    const pc = await newDevice();
    const other = await newDevice();
    const token = await idToken(await googleClaims(other));
    const body = JSON.stringify({ provider: "google", idToken: token, nonce: "the-salt", sign: other.sign, dh: other.dh });
    const headers = { ...(await signHeaders(pc, "POST", "/v1/auth/signin", body)), "CF-Connecting-IP": randomAddress() };
    const { cache } = testProvider();

    const response = await handleSignin(new Request("https://example.com/v1/auth/signin", { method: "POST", headers, body }), testEnv, { jwks: cache });
    expect(response.status).toBe(401);
  });

  it("gives 400 for an unknown provider or a malformed body, and 503 for a provider not set up", async () => {
    const pc = await newDevice();
    const { cache } = testProvider();
    const token = await idToken(await googleClaims(pc));

    expect((await handleSignin(await signinRequest(pc, "facebook", token), testEnv, { jwks: cache })).status).toBe(400);

    const noGoogle = { ...testEnv, GOOGLE_CLIENT_ID: "" } as Cloudflare.Env;
    expect((await handleSignin(await signinRequest(pc, "google", token), noGoogle, { jwks: cache })).status).toBe(503);

    const signed = await SELF.fetch(await signedRequest(pc, "POST", "/v1/auth/signin", "{}"));
    expect(signed.status).toBe(400);

    // An unsigned one is refused on its headers, before its body is read.
    const unsigned = await SELF.fetch("https://example.com/v1/auth/signin", {
      method: "POST",
      headers: { "CF-Connecting-IP": randomAddress() },
      body: "{}",
    });
    expect(unsigned.status).toBe(401);
  });

  it("gives 503 when the provider's keys can't be fetched", async () => {
    const failing = new JwksCache(async () => {
      throw new Error("offline");
    });
    const pc = await newDevice();
    const response = await handleSignin(await signinRequest(pc, "google", await idToken(await googleClaims(pc))), testEnv, { jwks: failing });
    expect(response.status).toBe(503);
  });
});

describe("JwksCache", () => {
  it("keeps a provider's keys for an hour", async () => {
    const { cache, fetched } = testProvider();
    const start = Date.now();

    expect(await cache.key(JWKS_URLS.google, "test-key", start)).toMatchObject({ kid: "test-key" });
    expect(await cache.key(JWKS_URLS.google, "test-key", start + 59 * 60_000)).toMatchObject({ kid: "test-key" });
    expect(fetched).toHaveLength(1);

    await cache.key(JWKS_URLS.google, "test-key", start + 60 * 60_000);
    expect(fetched).toHaveLength(2);
  });

  it("looks again for a key it hasn't seen, at most once a minute", async () => {
    const { cache, fetched } = testProvider();
    const start = Date.now();
    await cache.key(JWKS_URLS.google, "test-key", start);

    expect(await cache.key(JWKS_URLS.google, "rolled-in", start + 1000)).toBeNull();
    expect(fetched).toHaveLength(1);

    expect(await cache.key(JWKS_URLS.google, "rolled-in", start + 61_000)).toBeNull();
    expect(fetched).toHaveLength(2);
  });
});
