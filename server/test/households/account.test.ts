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

/** `by` moves the household's key on to `epoch`. */
async function rotate(hid: string, by: TestDevice, epoch: number): Promise<void> {
  const response = await signedFetch(by, "POST", `/v1/households/${hid}/keys`, { epoch, envelopes: [{ device: by.id, body: envelope() }] });
  expect(response.status).toBe(200);
}

/** A household whose first PC is signed in and has linked it to its account. */
async function linkedHousehold(): Promise<{ hid: string; owner: SignedIn }> {
  const owner = await signIn();
  const hid = await createHousehold(owner.device);
  const linked = await asAccount(owner, "POST", "/v1/account/household", { householdId: hid });
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

  it("needs householdId, naming a household the PC is a current member of", async () => {
    const member = await signIn();
    const hid = await createHousehold(member.device);
    expect((await asAccount(member, "POST", "/v1/account/household", {})).status).toBe(400);
    expect((await asAccount(member, "POST", "/v1/account/household", { household: hid })).status).toBe(400);
    expect((await asAccount(member, "POST", "/v1/account/household", { householdId: "not-an-id" })).status).toBe(400);

    const elsewhere = await createHousehold(await newDevice());
    expect((await asAccount(member, "POST", "/v1/account/household", { householdId: elsewhere })).status).toBe(403);
    expect(await env.DB.prepare("SELECT 1 FROM account_households WHERE account = ?").bind(member.account).first()).toBeNull();
  });

  it("gives 409 when the account is already linked to another household", async () => {
    const { owner } = await linkedHousehold();
    const other = await signIn(undefined, owner.account);
    const otherHid = await createHousehold(other.device);

    expect((await asAccount(other, "POST", "/v1/account/household", { householdId: otherHid })).status).toBe(409);
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
      {
        device: laptop.device.id,
        account: owner.account,
        sign: laptop.device.sign,
        dh: laptop.device.dh,
        created: expect.any(Number),
      },
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

  it("lets a member deny a waiting PC, which is then gone from the list", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    const deny = (by: TestDevice) => signedFetch(by, "DELETE", `/v1/households/${hid}/requests/${laptop.device.id}`);

    expect((await deny(await newDevice())).status).toBe(401);
    expect((await deny(owner.device)).status).toBe(200);
    expect(await (await signedFetch(owner.device, "GET", `/v1/households/${hid}/requests`)).json()).toEqual([]);
    expect((await deny(owner.device)).status).toBe(404);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
  });

  it("keeps at most 2 PCs of one account waiting", async () => {
    const { hid, owner } = await linkedHousehold();
    const pcs = [await signIn(undefined, owner.account), await signIn(undefined, owner.account), await signIn(undefined, owner.account)];

    expect((await asAccount(pcs[0], "POST", "/v1/account/requests")).status).toBe(200);
    expect((await asAccount(pcs[1], "POST", "/v1/account/requests")).status).toBe(200);
    expect((await asAccount(pcs[2], "POST", "/v1/account/requests")).status).toBe(409);
    expect((await asAccount(pcs[1], "POST", "/v1/account/requests")).status).toBe(200);

    const waiting = (await (await signedFetch(owner.device, "GET", `/v1/households/${hid}/requests`)).json()) as { device: string }[];
    expect(waiting.map((item) => item.device).sort()).toEqual([pcs[0].device.id, pcs[1].device.id].sort());
  });

  it("clears a PC's request when it's added directly, and when it signs out", async () => {
    const { hid, owner } = await linkedHousehold();
    const added = await signIn(undefined, owner.account);
    const leaving = await signIn(undefined, owner.account);
    await asAccount(added, "POST", "/v1/account/requests");
    await asAccount(leaving, "POST", "/v1/account/requests");

    await addMember(hid, owner.device, added.device);
    expect((await asAccount(leaving, "POST", "/v1/auth/signout")).status).toBe(200);

    expect(await env.DB.prepare("SELECT device FROM join_requests WHERE household = ?").bind(hid).all()).toMatchObject({ results: [] });
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

    expect((await signedFetch(stranger, "GET", `/v1/households/${hid}/requests`)).status).toBe(401);
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

  it("gives 409 for an approval sealing any epoch but the household's current one", async () => {
    const { hid, owner } = await linkedHousehold();
    const rotated = await signedFetch(owner.device, "POST", `/v1/households/${hid}/keys`, {
      epoch: 2,
      envelopes: [{ device: owner.device.id, body: envelope() }],
    });
    expect(rotated.status).toBe(200);
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    const approve = (epoch: number) =>
      signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, { epoch, body: envelope() });

    expect((await approve(1)).status).toBe(409);
    expect((await approve(3)).status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    expect((await approve(2)).status).toBe(200);
    expect(await isMember(hid, laptop.device.id)).toBe(true);
  });

  it("never overwrites an envelope: an approval whose PC already has one at the epoch is 409, and changes nothing", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    const kept = envelope();
    await env.DB.prepare(
      "INSERT INTO key_envelopes (household, epoch, device, from_device, body, created) VALUES (?, 1, ?, ?, ?, 1)",
    )
      .bind(hid, laptop.device.id, owner.device.id, kept)
      .run();

    const approve = await signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, {
      epoch: 1,
      body: envelope(),
    });
    expect(approve.status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    const row = await env.DB.prepare("SELECT body FROM key_envelopes WHERE household = ? AND device = ?")
      .bind(hid, laptop.device.id)
      .first<{ body: string }>();
    expect(row?.body).toBe(kept);
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
  it("keeps the envelope a member puts at the household's current epoch, and only a hash of the verifier", async () => {
    const { hid, owner } = await linkedHousehold();
    const body = envelope();
    const verifierBytes = crypto.getRandomValues(new Uint8Array(32));
    const verifier = base64urlEncode(verifierBytes);

    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body, verifier, epoch: 7 })).status).toBe(200);

    const newPc = await signIn(undefined, owner.account);
    const fetched = await asAccount(newPc, "GET", "/v1/account/recovery");
    expect(fetched.status).toBe(200);
    expect(await fetched.json()).toEqual({ householdId: hid, epoch: 1, body });

    const stored = await env.DB.prepare("SELECT * FROM recovery WHERE account = ?").bind(owner.account).first<Record<string, unknown>>();
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", verifierBytes));
    expect(stored?.verifier_hash).toBe([...digest].map((byte) => byte.toString(16).padStart(2, "0")).join(""));
    expect(Object.values(stored!)).not.toContain(verifier);

    await rotate(hid, owner.device, 2);
    const replaced = envelope();
    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body: replaced, verifier })).status).toBe(200);
    expect(await (await asAccount(newPc, "GET", "/v1/account/recovery")).json()).toEqual({ householdId: hid, epoch: 2, body: replaced });
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
    ]) {
      expect((await asAccount(owner, "PUT", "/v1/account/recovery", body)).status, JSON.stringify(body)).toBe(400);
    }
  });

  it("adds a PC that sends the verifier, with no approval", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier });
    const newPc = await signIn(undefined, owner.account);
    await asAccount(newPc, "POST", "/v1/account/requests");

    const recovered = await asAccount(newPc, "POST", "/v1/account/recover", { verifier });
    expect(recovered.status).toBe(200);
    expect(await recovered.json()).toEqual({ ok: true, householdId: hid });

    expect(await isMember(hid, newPc.device.id)).toBe(true);
    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE device = ?").bind(newPc.device.id).first()).toBeNull();
  });

  it("refuses another verifier, one from before the key changed, and an account with nothing to recover", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier });
    const newPc = await signIn(undefined, owner.account);
    const other = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));

    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier: other })).status).toBe(403);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", {})).status).toBe(400);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier: base64urlEncode(new Uint8Array(16)) })).status).toBe(400);

    await rotate(hid, owner.device, 2);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier })).status).toBe(409);
    expect(await isMember(hid, newPc.device.id)).toBe(false);

    const lonely = await signIn();
    expect((await asAccount(lonely, "POST", "/v1/account/recover", { verifier: other })).status).toBe(404);
  });
});

describe("removing a member", () => {
  it("ends the removed PC's session, and leaves the remover's alone", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, laptop.device);

    expect((await signedFetch(owner.device, "DELETE", `/v1/households/${hid}/members/${laptop.device.id}`)).status).toBe(200);

    expect((await asAccount(laptop, "POST", "/v1/account/requests")).status).toBe(401);
    expect((await asAccount(owner, "GET", "/v1/account/recovery")).status).toBe(404);
  });

  it("ends a leaving PC's session too", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, laptop.device);

    expect((await signedFetch(laptop.device, "DELETE", `/v1/households/${hid}/members/${laptop.device.id}`)).status).toBe(200);

    expect((await asAccount(laptop, "GET", "/v1/account/recovery")).status).toBe(401);
    expect((await asAccount(owner, "GET", "/v1/account/recovery")).status).toBe(404);
  });

  it("revokes every linked account's recovery, and unlinks the accounts the removed PC was signed in as", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier });
    // A second account, a family member's, linked to the same household, whose PC stays.
    const family = await signIn();
    await addMember(hid, owner.device, family.device);
    await asAccount(family, "POST", "/v1/account/household", { householdId: hid });
    await asAccount(family, "PUT", "/v1/account/recovery", { body: envelope(), verifier });
    const stolen = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, stolen.device);

    expect((await signedFetch(family.device, "DELETE", `/v1/households/${hid}/members/${stolen.device.id}`)).status).toBe(200);

    for (const account of [owner.account, family.account]) {
      expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(account).first(), account).toBeNull();
    }
    expect(await env.DB.prepare("SELECT 1 FROM account_households WHERE account = ?").bind(owner.account).first()).toBeNull();
    expect(await env.DB.prepare("SELECT household FROM account_households WHERE account = ?").bind(family.account).first())
      .toEqual({ household: hid });

    // Whoever holds the stolen PC's account can neither recover nor ask to join again.
    const thief = await signIn(undefined, owner.account);
    expect((await asAccount(thief, "POST", "/v1/account/recover", { verifier })).status).toBe(404);
    expect((await asAccount(thief, "POST", "/v1/account/requests")).status).toBe(409);
  });

  it("leaves a PC's session and link for its own household alone when another household removes it", async () => {
    const theirs = await newDevice();
    const theirHid = await createHousehold(theirs);
    const { hid: homeHid, owner: victim } = await linkedHousehold();
    await addMember(theirHid, theirs, victim.device);

    expect((await signedFetch(theirs, "DELETE", `/v1/households/${theirHid}/members/${victim.device.id}`)).status).toBe(200);

    expect((await asAccount(victim, "GET", "/v1/account/recovery")).status).toBe(404);
    expect(await env.DB.prepare("SELECT household FROM account_households WHERE account = ?").bind(victim.account).first())
      .toEqual({ household: homeHid });
  });

  it("clears a request the removed PC had waiting for the household", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, laptop.device);
    await env.DB.prepare(
      "INSERT INTO join_requests (household, device, account, sign_key, dh_key, created) VALUES (?, ?, ?, ?, ?, ?)",
    )
      .bind(hid, laptop.device.id, owner.account, laptop.device.sign, laptop.device.dh, Date.now())
      .run();

    await signedFetch(owner.device, "DELETE", `/v1/households/${hid}/members/${laptop.device.id}`);

    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE device = ?").bind(laptop.device.id).first()).toBeNull();
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

describe("POST /v1/auth/signout", () => {
  it("ends the session, and ending it again is done too", async () => {
    const who = await signIn();

    expect((await asAccount(who, "POST", "/v1/auth/signout")).status).toBe(200);
    expect(await env.DB.prepare("SELECT 1 FROM sessions WHERE device = ?").bind(who.device.id).first()).toBeNull();
    expect((await asAccount(who, "GET", "/v1/account/recovery")).status).toBe(401);

    expect((await asAccount(who, "POST", "/v1/auth/signout")).status).toBe(200);
  });

  it("needs the session, signed by its own PC", async () => {
    const who = await signIn();
    const other = await signIn();

    expect((await signedFetch(who.device, "POST", "/v1/auth/signout")).status).toBe(401);
    expect((await asAccount({ ...other, session: who.session }, "POST", "/v1/auth/signout")).status).toBe(401);
    expect(await env.DB.prepare("SELECT 1 FROM sessions WHERE device = ?").bind(who.device.id).first()).not.toBeNull();
  });
});

describe("DELETE /v1/account", () => {
  it("deletes the account, its link, sessions, requests and recovery; the household carries on", async () => {
    const { hid, owner } = await linkedHousehold();
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)) });
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    const bystander = await signIn();

    expect((await asAccount(owner, "DELETE", "/v1/account")).status).toBe(200);

    for (const [table, column] of [
      ["accounts", "id"],
      ["sessions", "account"],
      ["account_households", "account"],
      ["join_requests", "account"],
      ["recovery", "account"],
    ]) {
      const row = await env.DB.prepare(`SELECT 1 FROM ${table} WHERE ${column} = ?`).bind(owner.account).first();
      expect(row, table).toBeNull();
    }
    expect((await asAccount(laptop, "POST", "/v1/account/requests")).status).toBe(401);
    expect(await isMember(hid, owner.device.id)).toBe(true);
    expect((await signedFetch(owner.device, "GET", `/v1/households/${hid}/members`)).status).toBe(200);
    expect(await env.DB.prepare("SELECT 1 FROM accounts WHERE id = ?").bind(bystander.account).first()).not.toBeNull();
  });

  it("needs a current session", async () => {
    const who = await signIn();
    expect((await signedFetch(who.device, "DELETE", "/v1/account")).status).toBe(401);
    expect(await env.DB.prepare("SELECT 1 FROM accounts WHERE id = ?").bind(who.account).first()).not.toBeNull();
  });
});
