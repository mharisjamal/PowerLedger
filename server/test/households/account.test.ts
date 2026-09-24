import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { sha256hex } from "../../src/auth";
import { base64urlEncode } from "../../src/households/encoding";
import { runRetention } from "../../src/retention";
import { addMember, createHousehold, newDevice, signedFetch, type TestDevice } from "./support";

const DAY_MS = 24 * 60 * 60 * 1000;

interface SignedIn {
  device: TestDevice;
  account: string;
  session: string;
}

/** A PC signed in as `account` (a new one by default), as POST /v1/auth/signin leaves it. */
async function signIn(device?: TestDevice, account?: string): Promise<SignedIn> {
  const pc = device ?? (await newDevice());
  const id = account ?? crypto.randomUUID();
  const session = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
  await env.DB.batch([
    env.DB.prepare("INSERT OR IGNORE INTO accounts (id, provider, subject, created) VALUES (?, 'google', ?, 1)").bind(id, `sub-${id}`),
    env.DB.prepare("INSERT INTO sessions (token_hash, account, device, sign_key, dh_key, created) VALUES (?, ?, ?, ?, ?, 1)")
      .bind(await sha256hex(session), id, pc.id, pc.sign, pc.dh),
  ]);
  return { device: pc, account: id, session };
}

function asAccount(who: SignedIn, method: string, path: string, body?: unknown): Promise<Response> {
  return signedFetch(who.device, method, path, body, { headers: { Authorization: `Session ${who.session}` } });
}

function envelope(): string {
  return base64urlEncode(crypto.getRandomValues(new Uint8Array(60)));
}

async function proofFor(verifier: Uint8Array, deviceId: string): Promise<string> {
  const key = await crypto.subtle.importKey("raw", verifier, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return base64urlEncode(new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(deviceId))));
}

/** A household whose first PC is signed in and has linked it to its account. */
async function linkedHousehold(): Promise<{ hid: string; owner: SignedIn }> {
  const owner = await signIn();
  const hid = await createHousehold(owner.device);
  const linked = await asAccount(owner, "POST", "/v1/account/household", {});
  expect(linked.status).toBe(200);
  return { hid, owner };
}

async function isMember(hid: string, device: string): Promise<boolean> {
  const row = await env.DB.prepare("SELECT 1 FROM members WHERE household = ? AND device = ? AND removed IS NULL")
    .bind(hid, device)
    .first();
  return row !== null;
}

describe("the session on account routes", () => {
  it("is needed, must be current, and must be the signing PC's own", async () => {
    const who = await signIn();
    const other = await signIn();

    expect((await signedFetch(who.device, "GET", "/v1/account/recovery")).status).toBe(401);
    const unknown = { ...who, session: base64urlEncode(crypto.getRandomValues(new Uint8Array(32))) };
    expect((await asAccount(unknown, "GET", "/v1/account/recovery")).status).toBe(401);
    expect((await asAccount({ ...who, session: other.session }, "GET", "/v1/account/recovery")).status).toBe(401);
    expect((await asAccount(who, "GET", "/v1/account/recovery")).status).toBe(404);
  });
});

describe("POST /v1/account/household", () => {
  it("links the account to the household its PC is in, once", async () => {
    const { hid, owner } = await linkedHousehold();

    const row = await env.DB.prepare("SELECT household FROM account_households WHERE account = ?").bind(owner.account).first();
    expect(row).toEqual({ household: hid });

    const again = await asAccount(owner, "POST", "/v1/account/household", { householdId: hid });
    expect(again.status).toBe(200);
    expect(await again.json()).toEqual({ ok: true, householdId: hid });
  });

  it("refuses a PC in no household, and a household the PC isn't in", async () => {
    const lonely = await signIn();
    expect((await asAccount(lonely, "POST", "/v1/account/household", {})).status).toBe(409);

    const elsewhere = await createHousehold(await newDevice());
    expect((await asAccount(lonely, "POST", "/v1/account/household", { householdId: elsewhere })).status).toBe(403);
    expect((await asAccount(lonely, "POST", "/v1/account/household", { householdId: "not-an-id" })).status).toBe(400);
  });

  it("gives 409 when the account is already linked to another household", async () => {
    const { owner } = await linkedHousehold();
    const other = await signIn(undefined, owner.account);
    await createHousehold(other.device);

    expect((await asAccount(other, "POST", "/v1/account/household", {})).status).toBe(409);
  });
});

describe("join requests", () => {
  it("lets a PC signed in as the account ask, and members see and approve it with its envelope", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);

    const asked = await asAccount(laptop, "POST", "/v1/account/requests");
    expect(asked.status).toBe(200);
    expect(await asked.json()).toEqual({ ok: true, householdId: hid });

    const waiting = await signedFetch(owner.device, "GET", `/v1/households/${hid}/requests`);
    expect(waiting.status).toBe(200);
    expect(await waiting.json()).toEqual([
      { device: laptop.device.id, sign: laptop.device.sign, dh: laptop.device.dh, created: expect.any(Number) },
    ]);

    const body = envelope();
    const approved = await signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, {
      epoch: 1,
      body,
    });
    expect(approved.status).toBe(200);

    expect(await isMember(hid, laptop.device.id)).toBe(true);
    const key = await signedFetch(laptop.device, "GET", `/v1/households/${hid}/keys/1`);
    expect(await key.json()).toEqual({ epoch: 1, from: owner.device.id, body });
    expect(await (await signedFetch(owner.device, "GET", `/v1/households/${hid}/requests`)).json()).toEqual([]);
  });

  it("refuses to ask without a linked household, or when already in it", async () => {
    const lonely = await signIn();
    expect((await asAccount(lonely, "POST", "/v1/account/requests")).status).toBe(409);

    const { owner } = await linkedHousehold();
    expect((await asAccount(owner, "POST", "/v1/account/requests")).status).toBe(409);
  });

  it("shows requests only to members, and approves only waiting PCs", async () => {
    const { hid, owner } = await linkedHousehold();
    const stranger = await newDevice();

    expect((await signedFetch(stranger, "GET", `/v1/households/${hid}/requests`)).status).toBe(403);
    const approve = await signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${stranger.id}/approve`, {
      epoch: 1,
      body: envelope(),
    });
    expect(approve.status).toBe(404);
    expect(await isMember(hid, stranger.id)).toBe(false);
  });

  it("gives 400 for an approval without a well-formed envelope", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");

    for (const body of [{ epoch: 1 }, { epoch: "1", body: envelope() }, { epoch: 1, body: "AAAA" }]) {
      const response = await signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, body);
      expect(response.status, JSON.stringify(body)).toBe(400);
    }
    expect(await isMember(hid, laptop.device.id)).toBe(false);
  });

  it("keeps a request waiting when the household is full", async () => {
    const { hid, owner } = await linkedHousehold();
    for (let i = 0; i < 15; i++) await addMember(hid, owner.device, await newDevice());
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");

    const approve = await signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, {
      epoch: 1,
      body: envelope(),
    });
    expect(approve.status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    expect(await env.DB.prepare("SELECT 1 FROM key_envelopes WHERE device = ?").bind(laptop.device.id).first()).toBeNull();
    expect(await (await signedFetch(owner.device, "GET", `/v1/households/${hid}/requests`)).json()).toHaveLength(1);
  });

  it("are cleared by the daily cron after 7 days", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");

    await runRetention(env, new Date(Date.now() + 8 * DAY_MS));

    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE household = ?").bind(hid).first()).toBeNull();
  });
});

describe("recovery", () => {
  it("keeps the envelope a member puts, for any PC signed in as the account, without the verifier", async () => {
    const { hid, owner } = await linkedHousehold();
    const body = envelope();
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));

    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body, verifier, epoch: 2 })).status).toBe(200);

    const newPc = await signIn(undefined, owner.account);
    const fetched = await asAccount(newPc, "GET", "/v1/account/recovery");
    expect(fetched.status).toBe(200);
    expect(await fetched.json()).toEqual({ householdId: hid, epoch: 2, body });

    const replaced = envelope();
    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body: replaced, verifier })).status).toBe(200);
    expect(await (await asAccount(newPc, "GET", "/v1/account/recovery")).json()).toEqual({ householdId: hid, epoch: null, body: replaced });
  });

  it("is only put by a member of the linked household, with a 32-byte verifier", async () => {
    const lonely = await signIn();
    const good = { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)) };
    expect((await asAccount(lonely, "PUT", "/v1/account/recovery", good)).status).toBe(409);

    const { owner } = await linkedHousehold();
    const outsider = await signIn(undefined, owner.account);
    expect((await asAccount(outsider, "PUT", "/v1/account/recovery", good)).status).toBe(403);

    for (const body of [
      { body: envelope() },
      { body: envelope(), verifier: base64urlEncode(new Uint8Array(16)) },
      { body: "AAAA", verifier: good.verifier },
      { body: envelope(), verifier: good.verifier, epoch: -1 },
    ]) {
      expect((await asAccount(owner, "PUT", "/v1/account/recovery", body)).status, JSON.stringify(body)).toBe(400);
    }
  });

  it("adds a PC that proves it holds the household key, with no approval", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = crypto.getRandomValues(new Uint8Array(32));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(verifier), epoch: 1 });
    const newPc = await signIn(undefined, owner.account);
    await asAccount(newPc, "POST", "/v1/account/requests");

    const recovered = await asAccount(newPc, "POST", "/v1/account/recover", { proof: await proofFor(verifier, newPc.device.id) });
    expect(recovered.status).toBe(200);
    expect(await recovered.json()).toEqual({ ok: true, householdId: hid });

    expect(await isMember(hid, newPc.device.id)).toBe(true);
    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE device = ?").bind(newPc.device.id).first()).toBeNull();
  });

  it("refuses a proof made for another PC or with another key, and an account with nothing to recover", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = crypto.getRandomValues(new Uint8Array(32));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(verifier) });
    const newPc = await signIn(undefined, owner.account);

    const forOwner = await asAccount(newPc, "POST", "/v1/account/recover", { proof: await proofFor(verifier, owner.device.id) });
    expect(forOwner.status).toBe(403);
    const otherKey = await proofFor(crypto.getRandomValues(new Uint8Array(32)), newPc.device.id);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { proof: otherKey })).status).toBe(403);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", {})).status).toBe(400);
    expect(await isMember(hid, newPc.device.id)).toBe(false);

    const lonely = await signIn();
    expect((await asAccount(lonely, "POST", "/v1/account/recover", { proof: otherKey })).status).toBe(404);
  });
});

describe("a household that ends", () => {
  it("takes its account links, join requests and recovery with it", async () => {
    const { hid, owner } = await linkedHousehold();
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)) });
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");

    expect((await signedFetch(owner.device, "DELETE", `/v1/households/${hid}/members/${owner.device.id}`)).status).toBe(200);

    expect(await env.DB.prepare("SELECT 1 FROM account_households WHERE account = ?").bind(owner.account).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE household = ?").bind(hid).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(owner.account).first()).toBeNull();
  });
});
