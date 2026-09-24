import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import {
  deviceIdOf,
  MAX_REQUESTS_PER_DAY,
  pathAndQuery,
  requestToSign,
  verifySignature,
  verifySigned,
  verifySignedByKey,
} from "../../src/households/auth";
import { base64urlDecode } from "../../src/households/encoding";
import vectors from "../fixtures/households/vectors.json";
import { aliasedDevice, newDevice, randomHouseholdId, signedRequest } from "./support";

const vectorBody = base64urlDecode(vectors.request.body)!;
const vectorNow = vectors.request.time * 1000;

function vectorRequest(overrides: { path?: string; body?: Uint8Array } = {}): { request: Request; body: Uint8Array } {
  const body = overrides.body ?? vectorBody;
  const request = new Request(`https://example.com${overrides.path ?? vectors.request.path}`, {
    method: vectors.request.method,
    headers: {
      "X-PL-Device": vectors.deviceId,
      "X-PL-Time": String(vectors.request.time),
      "X-PL-Signature": vectors.request.signature,
    },
    body,
  });
  return { request, body };
}

async function seedMember(household: string, device: string, sign: string, removed: number | null = null): Promise<void> {
  await env.DB.prepare(
    `INSERT INTO members (household, device, sign_key, dh_key, added, removed) VALUES (?, ?, ?, 'dh', 1, ?)
     ON CONFLICT (household, device) DO UPDATE SET removed = excluded.removed`,
  )
    .bind(household, device, sign, removed)
    .run();
}

async function bodyOf(request: Request): Promise<Uint8Array> {
  return new Uint8Array(await request.clone().arrayBuffer());
}

describe("the .NET vectors", () => {
  it("give the same device ID from the signing key", async () => {
    expect(await deviceIdOf(base64urlDecode(vectors.signSpki)!)).toBe(vectors.deviceId);
  });

  it("cover the same text, and the signature verifies with WebCrypto", async () => {
    const text = requestToSign(vectors.request.method, vectors.request.path, vectors.request.time, await sha256(vectorBody));
    expect(new TextDecoder().decode(text)).toBe(vectors.request.signedText);
    expect(await verifySignature(vectors.signSpki, text, base64urlDecode(vectors.request.signature)!)).toBe(true);
  });

  it("don't verify with a flipped bit", async () => {
    const text = new TextEncoder().encode(vectors.request.signedText);
    const signature = base64urlDecode(vectors.request.signature)!;
    signature[10] ^= 1;
    expect(await verifySignature(vectors.signSpki, text, signature)).toBe(false);
  });
});

async function sha256(bytes: Uint8Array): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return [...digest].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

describe("pathAndQuery", () => {
  it("is the path and query as sent, an empty query's ? kept, as .NET's Uri.PathAndQuery gives", () => {
    expect(pathAndQuery(new Request("https://example.com/v1/households"))).toBe("/v1/households");
    expect(pathAndQuery(new Request("https://example.com/v1/h/batches?after=5&limit=100"))).toBe("/v1/h/batches?after=5&limit=100");
    expect(pathAndQuery(new Request("https://example.com/v1/h/batches?"))).toBe("/v1/h/batches?");
  });
});

describe("verifySigned", () => {
  const household = vectors.request.path.split("/")[3];

  it("gives the member row for the vector's request, once only", async () => {
    await seedMember(household, vectors.deviceId, vectors.signSpki);

    const first = vectorRequest();
    const member = await verifySigned(first.request, env, first.body, { now: vectorNow });
    expect(member).not.toBeInstanceOf(Response);
    expect(member).toMatchObject({ household, device: vectors.deviceId, sign_key: vectors.signSpki, removed: null });

    const again = vectorRequest();
    const replayed = await verifySigned(again.request, env, again.body, { now: vectorNow });
    expect((replayed as Response).status).toBe(401);
  });

  it("refuses a changed body", async () => {
    await seedMember(household, vectors.deviceId, vectors.signSpki);
    const changed = new Uint8Array(vectorBody);
    changed[0] ^= 1;
    const { request, body } = vectorRequest({ body: changed });

    const result = await verifySigned(request, env, body, { now: vectorNow });
    expect((result as Response).status).toBe(401);
  });

  it("refuses a time more than 300 s off, either way", async () => {
    await seedMember(household, vectors.deviceId, vectors.signSpki);

    for (const now of [vectorNow + 301_000, vectorNow - 301_000]) {
      const { request, body } = vectorRequest();
      const result = await verifySigned(request, env, body, { now });
      expect((result as Response).status).toBe(401);
    }
  });

  it("refuses a PC that isn't a member of the path's household", async () => {
    const { request, body } = vectorRequest({ path: `/v1/households/${randomHouseholdId()}/batches` });
    const result = await verifySigned(request, env, body, { now: vectorNow });
    expect((result as Response).status).toBe(401);
  });

  it("refuses a removed member", async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign, Date.now());

    const request = await signedRequest(device, "GET", `/v1/households/${hid}/members`);
    const result = await verifySigned(request, env, await bodyOf(request));
    expect((result as Response).status).toBe(410);
    expect(await (result as Response).json()).toEqual({ error: "This PC was removed from the household." });
  });

  it("gives no member row and a bad signature the same 401, telling a removed PC so only when its signature is good", async () => {
    const removed = await newDevice();
    const stranger = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, removed.id, removed.sign, Date.now());

    const fromStranger = await signedRequest(stranger, "GET", `/v1/households/${hid}/members`);
    const noRow = (await verifySigned(fromStranger, env, new Uint8Array(0))) as Response;
    const forged = await signedRequest(removed, "POST", `/v1/households/${hid}/batches`, "{}", { signedBody: "{ }" });
    const badSignature = (await verifySigned(forged, env, await bodyOf(forged))) as Response;

    expect(noRow.status).toBe(401);
    expect(badSignature.status).toBe(401);
    expect(await noRow.json()).toEqual(await badSignature.json());
  });

  it("accepts a fresh request from a current member, 300 s off at most", async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign);

    const path = `/v1/households/${hid}/batches?after=0&limit=100`;
    const seconds = Math.floor(Date.now() / 1000);
    const request = await signedRequest(device, "GET", path, undefined, { time: seconds - 300 });
    const result = await verifySigned(request, env, await bodyOf(request), { now: seconds * 1000 });
    expect(result).toMatchObject({ household: hid, device: device.id });
  });

  it("refuses a request whose query was changed after signing", async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign);

    const signed = await signedRequest(device, "GET", `/v1/households/${hid}/batches?after=5`);
    const moved = new Request(`https://example.com/v1/households/${hid}/batches?after=0`, { headers: signed.headers });
    const result = await verifySigned(moved, env, new Uint8Array(0));
    expect((result as Response).status).toBe(401);
  });

  it("refuses missing or malformed headers", async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign);
    const path = `/v1/households/${hid}/members`;
    const good = await signedRequest(device, "GET", path);

    for (const [name, value] of [
      ["X-PL-Device", null],
      ["X-PL-Time", null],
      ["X-PL-Signature", null],
      ["X-PL-Device", "NOT-HEX"],
      ["X-PL-Time", "soon"],
      ["X-PL-Signature", "c2hvcnQ"],
    ] as const) {
      const headers = new Headers(good.headers);
      if (value === null) headers.delete(name);
      else headers.set(name, value);
      const result = await verifySigned(new Request(good.url, { headers }), env, new Uint8Array(0));
      expect((result as Response).status, `${name}=${value}`).toBe(401);
    }
  });

  it(`gives 429 past ${MAX_REQUESTS_PER_DAY} requests a day from one PC`, async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign);
    const today = new Date().toISOString().slice(0, 10);
    await env.DB.prepare("INSERT INTO device_requests (device, utc_day, count) VALUES (?, ?, ?)")
      .bind(device.id, today, MAX_REQUESTS_PER_DAY - 1)
      .run();

    const last = await signedRequest(device, "GET", `/v1/households/${hid}/members`);
    expect(await verifySigned(last, env, new Uint8Array(0))).not.toBeInstanceOf(Response);

    const over = await signedRequest(device, "GET", `/v1/households/${hid}/members`);
    expect((await verifySigned(over, env, new Uint8Array(0)) as Response).status).toBe(429);
  });

  it("doesn't count a replayed request against the PC's day", async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign);
    const request = await signedRequest(device, "GET", `/v1/households/${hid}/members`);
    expect(await verifySigned(request.clone(), env, new Uint8Array(0))).not.toBeInstanceOf(Response);

    for (let i = 0; i < 3; i++) {
      expect(((await verifySigned(request.clone(), env, new Uint8Array(0))) as Response).status).toBe(401);
    }

    const today = new Date().toISOString().slice(0, 10);
    const row = await env.DB.prepare("SELECT count FROM device_requests WHERE device = ? AND utc_day = ?")
      .bind(device.id, today)
      .first<{ count: number }>();
    expect(row?.count).toBe(1);
  });

  it("takes a day of 15-minute syncs: a PC's 400th request today is still fine", async () => {
    const device = await newDevice();
    const hid = randomHouseholdId();
    await seedMember(hid, device.id, device.sign);
    const today = new Date().toISOString().slice(0, 10);
    await env.DB.prepare("INSERT INTO device_requests (device, utc_day, count) VALUES (?, ?, 399)").bind(device.id, today).run();

    const request = await signedRequest(device, "GET", `/v1/households/${hid}/members`);
    expect(await verifySigned(request, env, new Uint8Array(0))).not.toBeInstanceOf(Response);
  });
});

describe("verifySignedByKey", () => {
  it("accepts a request signed by the posted key, whose ID it carries", async () => {
    const device = await newDevice();
    const request = await signedRequest(device, "POST", "/v1/households", "{}");
    const result = await verifySignedByKey(request, env, await bodyOf(request), device.sign);
    expect(result).toEqual({ device: device.id });
  });

  it("refuses a signature over another path, even by the right key", async () => {
    const { request, body } = vectorRequest({ path: "/v1/households" });
    const result = await verifySignedByKey(request, env, body, vectors.signSpki, { now: vectorNow });
    expect((result as Response).status).toBe(401);
  });

  it("refuses a key posted in an aliased encoding, even signed by it and naming the ID its bytes give", async () => {
    const aliased = await aliasedDevice(await newDevice());
    const request = await signedRequest(aliased, "POST", "/v1/households", "{}");
    const result = await verifySignedByKey(request, env, await bodyOf(request), aliased.sign);
    expect((result as Response).status).toBe(401);
  });

  it("refuses a device ID the posted key doesn't give", async () => {
    const device = await newDevice();
    const other = await newDevice();
    const request = await signedRequest(device, "POST", "/v1/households", "{}");
    const result = await verifySignedByKey(request, env, await bodyOf(request), other.sign);
    expect((result as Response).status).toBe(401);
  });
});
