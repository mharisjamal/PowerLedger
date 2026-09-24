/** Helpers for the household tests: PCs with real P-256 keys, and requests signed the way the service signs them. */
import { SELF } from "cloudflare:test";
import { base64urlEncode, hex } from "../../src/households/encoding";

export interface TestDevice {
  id: string;
  sign: string;
  dh: string;
  signPrivate: CryptoKey;
  dhPrivate: CryptoKey;
}

/** A PC's two key pairs, as DeviceKeys.Create() makes them, with the device ID its signing key gives. */
export async function newDevice(): Promise<TestDevice> {
  const sign = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const dh = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
  const signSpki = new Uint8Array((await crypto.subtle.exportKey("spki", sign.publicKey)) as ArrayBuffer);
  const dhSpki = new Uint8Array((await crypto.subtle.exportKey("spki", dh.publicKey)) as ArrayBuffer);
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", signSpki));
  return {
    id: hex(digest.slice(0, 16)),
    sign: base64urlEncode(signSpki),
    dh: base64urlEncode(dhSpki),
    signPrivate: sign.privateKey,
    dhPrivate: dh.privateKey,
  };
}

/** The same P-256 public key as `spki` (base64url, canonical), re-encoded with its point compressed: an alias WebCrypto
 * imports as the same key, though its bytes, and so a device ID hashed from them, differ. */
export function compressedSpki(spki: string): string {
  const canonical = Uint8Array.from(atob(spki.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  const algorithm = canonical.slice(2, 23); // SEQUENCE { ecPublicKey, prime256v1 }
  const x = canonical.slice(27, 59);
  const yIsOdd = (canonical[90] & 1) === 1;
  const body = [...algorithm, 0x03, 0x22, 0x00, yIsOdd ? 0x03 : 0x02, ...x];
  return base64urlEncode(new Uint8Array([0x30, body.length, ...body]));
}

/** A PC whose signing key is posted in its compressed alias, with the device ID those bytes give. */
export async function aliasedDevice(device: TestDevice): Promise<TestDevice> {
  const sign = compressedSpki(device.sign);
  const bytes = Uint8Array.from(atob(sign.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return { ...device, sign, id: hex(digest.slice(0, 16)) };
}

export function randomHouseholdId(): string {
  return hex(crypto.getRandomValues(new Uint8Array(16)));
}

/** A fresh address per request, so no test shares the per-address rate limit with another. */
export function randomAddress(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(4));
  return `10.${bytes[0]}.${bytes[1]}.${bytes[2]}`;
}

function toBytes(body: string | Uint8Array | undefined): Uint8Array {
  if (body === undefined) return new Uint8Array(0);
  return typeof body === "string" ? new TextEncoder().encode(body) : body;
}

export interface SignOptions {
  /** Unix seconds; now by default. */
  time?: number;
  /** Signs this body but sends `body`, to make the signature not match. */
  signedBody?: string | Uint8Array;
  headers?: Record<string, string>;
}

/** The X-PL-* headers for a request, signed over METHOD \n path \n time \n hex SHA-256(body). */
export async function signHeaders(
  device: TestDevice,
  method: string,
  pathAndQuery: string,
  body: string | Uint8Array | undefined,
  options: SignOptions = {},
): Promise<Record<string, string>> {
  const time = options.time ?? Math.floor(Date.now() / 1000);
  const signedBytes = toBytes(options.signedBody ?? body);
  const bodyHash = hex(new Uint8Array(await crypto.subtle.digest("SHA-256", signedBytes)));
  const text = `${method.toUpperCase()}\n${pathAndQuery}\n${time}\n${bodyHash}`;
  const signature = await crypto.subtle.sign(
    { name: "ECDSA", hash: "SHA-256" },
    device.signPrivate,
    new TextEncoder().encode(text),
  );
  return {
    "X-PL-Device": device.id,
    "X-PL-Time": String(time),
    "X-PL-Signature": base64urlEncode(new Uint8Array(signature)),
  };
}

/** A signed Request, not yet sent, for calling a handler directly. */
export async function signedRequest(
  device: TestDevice,
  method: string,
  pathAndQuery: string,
  body?: string | Uint8Array,
  options: SignOptions = {},
): Promise<Request> {
  const headers = {
    ...(await signHeaders(device, method, pathAndQuery, body, options)),
    "CF-Connecting-IP": randomAddress(),
    ...options.headers,
  };
  return new Request(`https://example.com${pathAndQuery}`, {
    method,
    headers,
    body: body === undefined ? undefined : toBytes(body),
  });
}

/** Sends a signed request through the Worker. A JSON value is sent as JSON. */
export async function signedFetch(
  device: TestDevice,
  method: string,
  pathAndQuery: string,
  body?: unknown,
  options: SignOptions = {},
): Promise<Response> {
  const bytes =
    body === undefined ? undefined : body instanceof Uint8Array ? body : typeof body === "string" ? body : JSON.stringify(body);
  const request = await signedRequest(device, method, pathAndQuery, bytes, options);
  return SELF.fetch(request);
}

/** Creates a household with `device` as its first member; its ID. */
export async function createHousehold(device: TestDevice): Promise<string> {
  const id = randomHouseholdId();
  const response = await signedFetch(device, "POST", "/v1/households", { id, sign: device.sign, dh: device.dh });
  if (response.status !== 200) throw new Error(`create gave ${response.status}: ${await response.text()}`);
  return id;
}

/** The joining PC's proof that it holds its keys and asks to join this household: its signature over UTF-8
 * "powerledger join|{hid}|{sign}|{dh}", the keys as the base64url posted. */
export async function joinProof(device: TestDevice, householdId: string, sign = device.sign, dh = device.dh): Promise<string> {
  const statement = new TextEncoder().encode(`powerledger join|${householdId}|${sign}|${dh}`);
  const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, device.signPrivate, statement);
  return base64urlEncode(new Uint8Array(signature));
}

const REAL = Symbol("the real statement");

/**
 * `target` with `hook` run once, just before the first statement whose SQL matches `pattern` is executed, alone or in a
 * batch: the moment between a handler's check and its write, for a race to happen in. The hook uses the real database.
 */
export function hookBefore(target: Cloudflare.Env, pattern: RegExp, hook: () => Promise<unknown>): Cloudflare.Env {
  let fired = false;
  const sqlOf = new WeakMap<object, string>();
  const fire = async (sql: string | undefined) => {
    if (!fired && sql !== undefined && pattern.test(sql)) {
      fired = true;
      await hook();
    }
  };
  const wrap = (statement: D1PreparedStatement, sql: string): D1PreparedStatement => {
    const wrapped = new Proxy(statement, {
      get(object, property) {
        if (property === REAL) return object;
        if (property === "bind") return (...values: unknown[]) => wrap(object.bind(...values), sql);
        if (property === "first" || property === "run" || property === "all" || property === "raw") {
          return async (...args: unknown[]) => {
            await fire(sql);
            return (object as unknown as Record<string, (...rest: unknown[]) => unknown>)[property](...args);
          };
        }
        const value = Reflect.get(object, property);
        return typeof value === "function" ? value.bind(object) : value;
      },
    });
    sqlOf.set(wrapped, sql);
    return wrapped;
  };
  const db = new Proxy(target.DB, {
    get(object, property) {
      if (property === "prepare") return (sql: string) => wrap(object.prepare(sql), sql);
      if (property === "batch") {
        return async (statements: D1PreparedStatement[]) => {
          for (const statement of statements) await fire(sqlOf.get(statement));
          const real = statements.map((statement) => (statement as unknown as Record<symbol, D1PreparedStatement>)[REAL] ?? statement);
          return object.batch(real);
        };
      }
      const value = Reflect.get(object, property);
      return typeof value === "function" ? value.bind(object) : value;
    },
  });
  return { ...target, DB: db };
}

/** `by`, a member, adds `device`, with its proof. */
export async function addMember(householdId: string, by: TestDevice, device: TestDevice): Promise<void> {
  const body = { sign: device.sign, dh: device.dh, proof: await joinProof(device, householdId) };
  const response = await signedFetch(by, "POST", `/v1/households/${householdId}/members`, body);
  if (response.status !== 200) throw new Error(`add gave ${response.status}: ${await response.text()}`);
}
