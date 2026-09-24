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

/** `by`, a member, adds `device`. */
export async function addMember(householdId: string, by: TestDevice, device: TestDevice): Promise<void> {
  const response = await signedFetch(by, "POST", `/v1/households/${householdId}/members`, { sign: device.sign, dh: device.dh });
  if (response.status !== 200) throw new Error(`add gave ${response.status}: ${await response.text()}`);
}
