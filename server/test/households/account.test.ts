import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { sha256hex } from "../../src/auth";
import { base64urlEncode } from "../../src/households/encoding";
import { handleHouseholdRoutes } from "../../src/households/routes";
import { runRetention } from "../../src/retention";
import { addMember, createHousehold, hookBefore, newDevice, signedFetch, signedRequest, type TestDevice } from "./support";

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
async function rotate(hid: string, by: TestDevice, epoch: number, to: TestDevice[] = [by]): Promise<void> {
  const envelopes = to.map((pc) => ({ device: pc.id, body: envelope() }));
  const response = await signedFetch(by, "POST", `/v1/households/${hid}/keys`, { epoch, envelopes });
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

function nonce(): string {
  return base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
}

interface MemberRequest {
  device: string;
  account: string;
  sign: string;
  dh: string;
  created: number;
  approver: string | null;
  commit: string | null;
  nonce: string | null;
  reveal: string | null;
}

interface OwnRequest {
  device: string;
  household: string;
  approver: { device: string; sign: string; dh: string } | null;
  commit: string | null;
  reveal: string | null;
  approved: { epoch: number } | null;
  expires: number;
}

async function memberList(hid: string, by: TestDevice): Promise<MemberRequest[]> {
  const response = await signedFetch(by, "GET", `/v1/households/${hid}/requests`);
  expect(response.status).toBe(200);
  return response.json();
}

async function ownRequests(who: SignedIn): Promise<OwnRequest[]> {
  const response = await asAccount(who, "GET", "/v1/account/requests");
  expect(response.status).toBe(200);
  return ((await response.json()) as { requests: OwnRequest[] }).requests;
}

function commitAs(hid: string, by: TestDevice, pc: SignedIn, commit = nonce()): Promise<Response> {
  return signedFetch(by, "POST", `/v1/households/${hid}/requests/${pc.device.id}/commit`, { commit });
}

function nonceAs(pc: SignedIn, value = nonce()): Promise<Response> {
  return asAccount(pc, "POST", "/v1/account/requests/nonce", { nonce: value });
}

function revealAs(hid: string, by: TestDevice, pc: SignedIn, value = nonce()): Promise<Response> {
  return signedFetch(by, "POST", `/v1/households/${hid}/requests/${pc.device.id}/reveal`, { nonce: value });
}

function approveAs(hid: string, by: TestDevice, pc: SignedIn, epoch = 1, body = envelope()): Promise<Response> {
  return signedFetch(by, "POST", `/v1/households/${hid}/requests/${pc.device.id}/approve`, { epoch, body });
}

/** `by` commits, `pc` answers with its nonce, `by` reveals: all an approval needs before it. */
async function readyToApprove(hid: string, by: TestDevice, pc: SignedIn): Promise<void> {
  expect((await commitAs(hid, by, pc)).status).toBe(200);
  expect((await nonceAs(pc)).status).toBe(200);
  expect((await revealAs(hid, by, pc)).status).toBe(200);
}

describe("join requests", () => {
  it("run ask, commit, nonce, reveal and approve, each side seeing the other's steps, the PC reading its approval once", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);

    const asked = await asAccount(laptop, "POST", "/v1/account/requests");
    expect(asked.status).toBe(200);
    expect(await asked.json()).toEqual({ ok: true, householdId: hid });
    const mine = await ownRequests(laptop);
    expect(mine).toEqual([
      { device: laptop.device.id, household: hid, approver: null, commit: null, reveal: null, approved: null, expires: expect.any(Number) },
    ]);
    expect(mine[0].expires).toBeGreaterThan(Date.now() + 23 * 60 * 60 * 1000);
    expect(mine[0].expires).toBeLessThanOrEqual(Date.now() + 24 * 60 * 60 * 1000);
    expect(await memberList(hid, owner.device)).toEqual([
      {
        device: laptop.device.id,
        account: owner.account,
        sign: laptop.device.sign,
        dh: laptop.device.dh,
        created: expect.any(Number),
        approver: null,
        commit: null,
        nonce: null,
        reveal: null,
      },
    ]);

    const commit = nonce();
    expect((await commitAs(hid, owner.device, laptop, commit)).status).toBe(200);
    expect(await ownRequests(laptop)).toMatchObject([
      { approver: { device: owner.device.id, sign: owner.device.sign, dh: owner.device.dh }, commit, reveal: null, approved: null },
    ]);

    const theirs = nonce();
    expect((await nonceAs(laptop, theirs)).status).toBe(200);
    expect(await memberList(hid, owner.device)).toMatchObject([{ approver: owner.device.id, commit, nonce: theirs, reveal: null }]);

    const reveal = nonce();
    expect((await revealAs(hid, owner.device, laptop, reveal)).status).toBe(200);
    expect(await ownRequests(laptop)).toMatchObject([{ commit, reveal }]);

    const body = envelope();
    expect((await approveAs(hid, owner.device, laptop, 1, body)).status).toBe(200);
    expect(await isMember(hid, laptop.device.id)).toBe(true);
    expect(await (await signedFetch(laptop.device, "GET", `/v1/households/${hid}/keys/1`)).json()).toEqual({
      epoch: 1,
      from: owner.device.id,
      body,
    });
    expect(await memberList(hid, owner.device)).toEqual([]);

    // Reading the approval keeps it, however often: only the PC's own withdrawal, once it has entered, ends it.
    expect(await ownRequests(laptop)).toMatchObject([{ approved: { epoch: 1 } }]);
    expect(await ownRequests(laptop)).toMatchObject([{ approved: { epoch: 1 } }]);
    expect((await asAccount(laptop, "DELETE", "/v1/account/requests")).status).toBe(200);
    expect(await ownRequests(laptop)).toEqual([]);
    expect((await asAccount(laptop, "DELETE", "/v1/account/requests")).status).toBe(200);
  });

  it("keep an approved request 7 days from its approval, and won't let a member deny it", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);
    expect((await approveAs(hid, owner.device, laptop)).status).toBe(200);

    expect((await signedFetch(owner.device, "DELETE", `/v1/households/${hid}/requests/${laptop.device.id}`)).status).toBe(409);

    const day = 24 * 60 * 60 * 1000;
    const approvedAt = Date.now() - 6 * day;
    await env.DB.prepare("UPDATE join_requests SET created = ?, approved_at = ? WHERE device = ?")
      .bind(Date.now() - 30 * day, approvedAt, laptop.device.id)
      .run();
    expect(await ownRequests(laptop)).toMatchObject([{ approved: { epoch: 1 }, expires: approvedAt + 7 * day }]);
    await runRetention(env, new Date());
    expect(await ownRequests(laptop)).toHaveLength(1);

    await env.DB.prepare("UPDATE join_requests SET approved_at = ? WHERE device = ?").bind(Date.now() - 8 * day, laptop.device.id).run();
    expect(await ownRequests(laptop)).toEqual([]);
    await runRetention(env, new Date());
    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE device = ?").bind(laptop.device.id).first()).toBeNull();
  });

  it("are withdrawn by the waiting PC itself, as when its user says the codes don't match", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await commitAs(hid, owner.device, laptop);

    expect((await asAccount(laptop, "DELETE", "/v1/account/requests")).status).toBe(200);

    expect(await memberList(hid, owner.device)).toEqual([]);
    expect(await ownRequests(laptop)).toEqual([]);
  });

  it("take each step once: an identical retry by the same PC is done, anything else 409", async () => {
    const { hid, owner } = await linkedHousehold();
    const second = await newDevice();
    await addMember(hid, owner.device, second);
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");

    const commit = nonce();
    expect((await commitAs(hid, owner.device, laptop, commit)).status).toBe(200);
    expect((await commitAs(hid, owner.device, laptop, commit)).status).toBe(200);
    expect((await commitAs(hid, owner.device, laptop)).status).toBe(409);
    expect((await commitAs(hid, second, laptop)).status).toBe(409);

    const theirs = nonce();
    expect((await nonceAs(laptop, theirs)).status).toBe(200);
    expect((await nonceAs(laptop, theirs)).status).toBe(200);
    expect((await nonceAs(laptop)).status).toBe(409);

    const reveal = nonce();
    expect((await revealAs(hid, owner.device, laptop, reveal)).status).toBe(200);
    expect((await revealAs(hid, owner.device, laptop, reveal)).status).toBe(200);
    expect((await revealAs(hid, owner.device, laptop)).status).toBe(409);
    expect(await memberList(hid, owner.device)).toMatchObject([{ commit, nonce: theirs, reveal }]);
  });

  it("keep their order: the nonce after a commit, the reveal after the nonce, the approval after the reveal", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");

    expect((await nonceAs(laptop)).status).toBe(409);
    expect((await commitAs(hid, owner.device, laptop)).status).toBe(200);
    expect((await revealAs(hid, owner.device, laptop)).status).toBe(409);
    expect((await approveAs(hid, owner.device, laptop)).status).toBe(409);
    expect((await nonceAs(laptop)).status).toBe(200);
    expect((await approveAs(hid, owner.device, laptop)).status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    expect((await revealAs(hid, owner.device, laptop)).status).toBe(200);
    expect((await approveAs(hid, owner.device, laptop)).status).toBe(200);
  });

  it("approve only if nothing changed since the approval looked: a rotation, a denial or an ask-again between gives 409", async () => {
    type Race = { hid: string; owner: SignedIn; second: TestDevice; laptop: SignedIn };
    const changes: [string, (race: Race) => Promise<unknown>][] = [
      ["a rotation", (race) => rotate(race.hid, race.owner.device, 2, [race.owner.device, race.second])],
      ["a denial", (race) => signedFetch(race.second, "DELETE", `/v1/households/${race.hid}/requests/${race.laptop.device.id}`)],
      ["an ask-again", (race) => asAccount(race.laptop, "POST", "/v1/account/requests")],
    ];
    for (const [name, change] of changes) {
      const { hid, owner } = await linkedHousehold();
      const second = await newDevice();
      await addMember(hid, owner.device, second);
      const laptop = await signIn(undefined, owner.account);
      await asAccount(laptop, "POST", "/v1/account/requests");
      await readyToApprove(hid, owner.device, laptop);
      const raced = hookBefore(env, /INSERT INTO members|INSERT INTO key_envelopes|UPDATE join_requests SET approved/, () =>
        change({ hid, owner, second, laptop }),
      );
      const body = JSON.stringify({ epoch: 1, body: envelope() });
      const request = await signedRequest(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, body);

      expect((await handleHouseholdRoutes(request, raced))!.status, name).toBe(409);
      expect(await isMember(hid, laptop.device.id), name).toBe(false);
      expect(await env.DB.prepare("SELECT 1 FROM key_envelopes WHERE device = ?").bind(laptop.device.id).first(), name).toBeNull();
    }
  });

  it("take an identical approval retry as done, and any other once approved as 409", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);
    const body = envelope();

    expect((await approveAs(hid, owner.device, laptop, 1, body)).status).toBe(200);
    expect((await approveAs(hid, owner.device, laptop, 1, body)).status).toBe(200);
    expect((await approveAs(hid, owner.device, laptop, 1, envelope())).status).toBe(409);
    expect((await approveAs(hid, owner.device, (await signIn(undefined, owner.account)), 1, body)).status).toBe(404);
  });

  it("let only the member that committed reveal and approve", async () => {
    const { hid, owner } = await linkedHousehold();
    const second = await newDevice();
    await addMember(hid, owner.device, second);
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await commitAs(hid, owner.device, laptop);
    await nonceAs(laptop);

    expect((await revealAs(hid, second, laptop)).status).toBe(403);
    expect((await revealAs(hid, owner.device, laptop)).status).toBe(200);
    expect((await approveAs(hid, second, laptop)).status).toBe(403);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    expect((await approveAs(hid, owner.device, laptop)).status).toBe(200);
  });

  it("give 400 for a commit, nonce or reveal that isn't 32 bytes, and 404 for a PC not waiting", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    const short = base64urlEncode(new Uint8Array(16));

    expect((await commitAs(hid, owner.device, laptop)).status).toBe(404);
    expect((await nonceAs(laptop)).status).toBe(404);
    await asAccount(laptop, "POST", "/v1/account/requests");

    expect((await commitAs(hid, owner.device, laptop, short)).status).toBe(400);
    await commitAs(hid, owner.device, laptop);
    expect((await nonceAs(laptop, short)).status).toBe(400);
    await nonceAs(laptop);
    expect((await revealAs(hid, owner.device, laptop, "not base64url!")).status).toBe(400);
  });

  it("let a member deny a waiting PC, which is then gone from the list", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    const deny = (by: TestDevice) => signedFetch(by, "DELETE", `/v1/households/${hid}/requests/${laptop.device.id}`);

    expect((await deny(await newDevice())).status).toBe(401);
    expect((await deny(owner.device)).status).toBe(200);
    expect(await memberList(hid, owner.device)).toEqual([]);
    expect(await ownRequests(laptop)).toEqual([]);
    expect((await deny(owner.device)).status).toBe(404);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
  });

  it("keep at most 2 PCs of one account waiting", async () => {
    const { hid, owner } = await linkedHousehold();
    const pcs = [await signIn(undefined, owner.account), await signIn(undefined, owner.account), await signIn(undefined, owner.account)];

    expect((await asAccount(pcs[0], "POST", "/v1/account/requests")).status).toBe(200);
    expect((await asAccount(pcs[1], "POST", "/v1/account/requests")).status).toBe(200);
    expect((await asAccount(pcs[2], "POST", "/v1/account/requests")).status).toBe(409);
    expect((await asAccount(pcs[1], "POST", "/v1/account/requests")).status).toBe(200);

    const waiting = await memberList(hid, owner.device);
    expect(waiting.map((item) => item.device).sort()).toEqual([pcs[0].device.id, pcs[1].device.id].sort());

    // An approved request, kept until its PC withdraws it, is no longer waiting.
    await readyToApprove(hid, owner.device, pcs[0]);
    expect((await approveAs(hid, owner.device, pcs[0])).status).toBe(200);
    expect((await asAccount(pcs[2], "POST", "/v1/account/requests")).status).toBe(200);
  });

  it("start afresh when a PC asks again", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await commitAs(hid, owner.device, laptop);

    await asAccount(laptop, "POST", "/v1/account/requests");

    expect(await memberList(hid, owner.device)).toMatchObject([{ approver: null, commit: null, nonce: null, reveal: null }]);
  });

  it("are cleared when the PC is added directly, and when it signs out", async () => {
    const { hid, owner } = await linkedHousehold();
    const added = await signIn(undefined, owner.account);
    const leaving = await signIn(undefined, owner.account);
    await asAccount(added, "POST", "/v1/account/requests");
    await asAccount(leaving, "POST", "/v1/account/requests");

    await addMember(hid, owner.device, added.device);
    expect((await asAccount(leaving, "POST", "/v1/auth/signout")).status).toBe(200);

    expect(await env.DB.prepare("SELECT device FROM join_requests WHERE household = ?").bind(hid).all()).toMatchObject({ results: [] });
  });

  it("can't be made without a linked household, or by a PC already in it", async () => {
    const lonely = await signIn();
    expect((await asAccount(lonely, "POST", "/v1/account/requests")).status).toBe(409);

    const { owner } = await linkedHousehold();
    expect((await asAccount(owner, "POST", "/v1/account/requests")).status).toBe(409);
  });

  it("are shown only to members, and only waiting PCs are approved", async () => {
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

  it("take an approval's sealed key and member list up to 16384 characters", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);

    expect((await approveAs(hid, owner.device, laptop, 1, "A".repeat(16385))).status).toBe(400);
    expect((await approveAs(hid, owner.device, laptop, 1, "A".repeat(16388))).status).toBe(400);
    expect((await approveAs(hid, owner.device, laptop, 1, "A".repeat(16384))).status).toBe(200);
  });

  it("give 400 for an approval without a well-formed envelope", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);

    for (const body of [{ epoch: 1 }, { epoch: "1", body: envelope() }, { epoch: 1, body: "AAAA" }]) {
      const response = await signedFetch(owner.device, "POST", `/v1/households/${hid}/requests/${laptop.device.id}/approve`, body);
      expect(response.status, JSON.stringify(body)).toBe(400);
    }
    expect(await isMember(hid, laptop.device.id)).toBe(false);
  });

  it("give 409 for an approval sealing any epoch but the household's current one", async () => {
    const { hid, owner } = await linkedHousehold();
    await rotate(hid, owner.device, 2);
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);

    expect((await approveAs(hid, owner.device, laptop, 1)).status).toBe(409);
    expect((await approveAs(hid, owner.device, laptop, 3)).status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    expect((await approveAs(hid, owner.device, laptop, 2)).status).toBe(200);
    expect(await isMember(hid, laptop.device.id)).toBe(true);
  });

  it("never overwrite an envelope: an approval whose PC already has one at the epoch is 409, and changes nothing", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);
    const kept = envelope();
    await env.DB.prepare(
      "INSERT INTO key_envelopes (household, epoch, device, from_device, body, created) VALUES (?, 1, ?, ?, ?, 1)",
    )
      .bind(hid, laptop.device.id, owner.device.id, kept)
      .run();

    expect((await approveAs(hid, owner.device, laptop)).status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    const row = await env.DB.prepare("SELECT body FROM key_envelopes WHERE household = ? AND device = ?")
      .bind(hid, laptop.device.id)
      .first<{ body: string }>();
    expect(row?.body).toBe(kept);
  });

  it("stay waiting when the household is full", async () => {
    const { hid, owner } = await linkedHousehold();
    for (let i = 0; i < 15; i++) await addMember(hid, owner.device, await newDevice());
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, laptop);

    expect((await approveAs(hid, owner.device, laptop)).status).toBe(409);
    expect(await isMember(hid, laptop.device.id)).toBe(false);
    expect(await env.DB.prepare("SELECT 1 FROM key_envelopes WHERE device = ?").bind(laptop.device.id).first()).toBeNull();
    expect(await memberList(hid, owner.device)).toHaveLength(1);
  });

  it("expire after 24 hours, and the daily cron clears them", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    await env.DB.prepare("UPDATE join_requests SET created = ? WHERE device = ?").bind(Date.now() - 25 * 60 * 60 * 1000, laptop.device.id).run();

    expect(await memberList(hid, owner.device)).toEqual([]);
    expect(await ownRequests(laptop)).toEqual([]);
    expect((await commitAs(hid, owner.device, laptop)).status).toBe(404);

    await runRetention(env, new Date());
    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE household = ?").bind(hid).first()).toBeNull();
  });
});

describe("recovery", () => {
  it("keeps what its holder puts at the household's current epoch, and only a hash of the verifier", async () => {
    const { hid, owner } = await linkedHousehold();
    const body = envelope();
    const verifierBytes = crypto.getRandomValues(new Uint8Array(32));
    const verifier = base64urlEncode(verifierBytes);

    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body, verifier, epoch: 1, replace: true })).status).toBe(200);

    const newPc = await signIn(undefined, owner.account);
    const fetched = await asAccount(newPc, "GET", "/v1/account/recovery");
    expect(fetched.status).toBe(200);
    expect(await fetched.json()).toEqual({ body, epoch: 1, holder: owner.device.id });

    const stored = await env.DB.prepare("SELECT * FROM recovery WHERE account = ?").bind(owner.account).first<Record<string, unknown>>();
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", verifierBytes));
    expect(stored?.verifier_hash).toBe([...digest].map((byte) => byte.toString(16).padStart(2, "0")).join(""));
    expect(Object.values(stored!)).not.toContain(verifier);

    await rotate(hid, owner.device, 2);
    const replaced = envelope();
    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body: replaced, verifier, epoch: 1 })).status).toBe(409);
    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body: replaced, verifier, epoch: 3 })).status).toBe(409);
    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body: replaced, verifier, epoch: 2 })).status).toBe(200);
    expect(await (await asAccount(newPc, "GET", "/v1/account/recovery")).json()).toEqual({ body: replaced, epoch: 2, holder: owner.device.id });
  });

  it("isn't kept when its PC is removed, or the key moves on, between the put's checks and its write", async () => {
    const { hid, owner } = await linkedHousehold();
    const other = await newDevice();
    await addMember(hid, owner.device, other);
    const put = async (epoch: number, replace: boolean, hook: () => Promise<unknown>) => {
      const body = JSON.stringify({ body: envelope(), verifier: nonce(), epoch, replace });
      const request = await signedRequest(owner.device, "PUT", "/v1/account/recovery", body, {
        headers: { Authorization: `Session ${owner.session}` },
      });
      return (await handleHouseholdRoutes(request, hookBefore(env, /INSERT INTO recovery|UPDATE recovery/, hook)))!.status;
    };

    expect(await put(1, true, () => rotate(hid, other, 2, [owner.device, other]))).toBe(409);
    expect((await asAccount(owner, "GET", "/v1/account/recovery")).status).toBe(404);

    expect((await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: nonce(), epoch: 2, replace: true })).status).toBe(200);
    expect(await put(2, false, () => signedFetch(other, "DELETE", `/v1/households/${hid}/members/${owner.device.id}`))).toBe(403);
    expect((await asAccount(owner, "GET", "/v1/account/recovery")).status).toBe(404);
  });

  it("isn't kept for a new holder removed between the put's checks and its write", async () => {
    const { hid, owner } = await linkedHousehold();
    const other = await newDevice();
    await addMember(hid, owner.device, other);
    const body = JSON.stringify({ body: envelope(), verifier: nonce(), epoch: 1, replace: true });
    const request = await signedRequest(owner.device, "PUT", "/v1/account/recovery", body, {
      headers: { Authorization: `Session ${owner.session}` },
    });
    const raced = hookBefore(env, /INSERT INTO recovery/, () =>
      signedFetch(other, "DELETE", `/v1/households/${hid}/members/${owner.device.id}`),
    );

    expect((await handleHouseholdRoutes(request, raced))!.status).toBe(403);
    expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(owner.account).first()).toBeNull();
  });

  it("is put only by its holder, unless replace hands it to a new holder with a new code", async () => {
    const { hid, owner } = await linkedHousehold();
    const second = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, second.device);
    const put = (who: SignedIn, replace?: boolean) =>
      asAccount(who, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)), epoch: 1, replace });

    expect((await put(owner)).status).toBe(409); // nothing to hold yet: a first code is a new code
    expect((await put(owner, true)).status).toBe(200);
    expect((await put(owner, false)).status).toBe(200);

    expect((await put(second)).status).toBe(409);
    expect((await put(second, true)).status).toBe(200);
    expect(((await (await asAccount(owner, "GET", "/v1/account/recovery")).json()) as { holder: string }).holder).toBe(second.device.id);
    expect((await put(owner)).status).toBe(409); // a newer code exists: the old holder forgets its key
  });

  it("takes a sealed key and member list up to 16384 characters", async () => {
    const { owner } = await linkedHousehold();
    const put = (body: string) =>
      asAccount(owner, "PUT", "/v1/account/recovery", { body, verifier: base64urlEncode(new Uint8Array(32)), epoch: 1, replace: true });

    expect((await put("A".repeat(16385))).status).toBe(400);
    expect((await put("A".repeat(16388))).status).toBe(400);
    expect((await put("A".repeat(16384))).status).toBe(200);
  });

  it("is only put by a current member of the linked household, well formed", async () => {
    const lonely = await signIn();
    const good = { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)), epoch: 1, replace: true };
    expect((await asAccount(lonely, "PUT", "/v1/account/recovery", good)).status).toBe(409);

    const { owner } = await linkedHousehold();
    const outsider = await signIn(undefined, owner.account);
    expect((await asAccount(outsider, "PUT", "/v1/account/recovery", good)).status).toBe(403);

    for (const body of [
      { ...good, body: undefined },
      { ...good, verifier: base64urlEncode(new Uint8Array(16)) },
      { ...good, body: "AAAA" },
      { ...good, epoch: undefined },
      { ...good, epoch: -1 },
      { ...good, replace: "yes" },
    ]) {
      expect((await asAccount(owner, "PUT", "/v1/account/recovery", body)).status, JSON.stringify(body)).toBe(400);
    }
  });

  it("makes the recovering PC the only current member at any stored epoch, and is used up", async () => {
    const { hid, owner } = await linkedHousehold();
    const other = await newDevice();
    await addMember(hid, owner.device, other);
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    await rotate(hid, owner.device, 2, [owner.device, other]);
    const newPc = await signIn(undefined, owner.account);
    await asAccount(newPc, "POST", "/v1/account/requests");

    const recovered = await asAccount(newPc, "POST", "/v1/account/recover", { verifier });
    expect(recovered.status).toBe(200);
    expect(await recovered.json()).toEqual({ household: hid, epoch: 2 });

    const rows = await env.DB.prepare("SELECT device, removed_epoch, added_epoch FROM members WHERE household = ? ORDER BY device")
      .bind(hid)
      .all<{ device: string; removed_epoch: number | null; added_epoch: number }>();
    const byDevice = new Map(rows.results.map((row) => [row.device, row]));
    expect(byDevice.get(newPc.device.id)).toMatchObject({ removed_epoch: null, added_epoch: 2 });
    expect(byDevice.get(owner.device.id)).toMatchObject({ removed_epoch: 2 });
    expect(byDevice.get(other.id)).toMatchObject({ removed_epoch: 2 });
    expect((await asAccount(newPc, "GET", "/v1/account/recovery")).status).toBe(404);
    expect(await env.DB.prepare("SELECT 1 FROM join_requests WHERE device = ?").bind(newPc.device.id).first()).toBeNull();
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier })).status).toBe(404);
  });

  it("uses up every recovery of the household, so another account's code can't bring its removed holder back", async () => {
    const { hid, owner } = await linkedHousehold();
    const family = await signIn();
    await addMember(hid, owner.device, family.device);
    await asAccount(family, "POST", "/v1/account/household", { householdId: hid });
    const ownerVerifier = nonce();
    const familyVerifier = nonce();
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: ownerVerifier, epoch: 1, replace: true });
    await asAccount(family, "PUT", "/v1/account/recovery", { body: envelope(), verifier: familyVerifier, epoch: 1, replace: true });

    const newPc = await signIn(undefined, owner.account);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier: ownerVerifier })).status).toBe(200);

    expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(family.account).first()).toBeNull();
    const familyPc = await signIn(undefined, family.account);
    expect((await asAccount(familyPc, "POST", "/v1/account/recover", { verifier: familyVerifier })).status).toBe(404);
    expect(await isMember(hid, newPc.device.id)).toBe(true);
  });

  it("recovers once when two PCs race: the one whose batch runs second finds the code used up and gets 404", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = nonce();
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    const first = await signIn(undefined, owner.account);
    const second = await signIn(undefined, owner.account);

    const raced = hookBefore(env, /UPDATE members SET removed/, async () => {
      expect((await asAccount(second, "POST", "/v1/account/recover", { verifier })).status).toBe(200);
    });
    const request = await signedRequest(first.device, "POST", "/v1/account/recover", JSON.stringify({ verifier }), {
      headers: { Authorization: `Session ${first.session}` },
    });
    expect((await handleHouseholdRoutes(request, raced))!.status).toBe(404);

    expect(await isMember(hid, second.device.id)).toBe(true);
    expect(await isMember(hid, first.device.id)).toBe(false);
    expect(await isMember(hid, owner.device.id)).toBe(false);
  });

  it("clears the requests of the PCs it removes: those they approved, and those they committed to", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = nonce();
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    const approved = await signIn(undefined, owner.account);
    await asAccount(approved, "POST", "/v1/account/requests");
    await readyToApprove(hid, owner.device, approved);
    expect((await approveAs(hid, owner.device, approved)).status).toBe(200);
    const waiting = await signIn(undefined, owner.account);
    await asAccount(waiting, "POST", "/v1/account/requests");
    expect((await commitAs(hid, approved.device, waiting)).status).toBe(200);

    const newPc = await signIn(undefined, owner.account);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier })).status).toBe(200);

    expect(await env.DB.prepare("SELECT device FROM join_requests WHERE household = ?").bind(hid).all()).toMatchObject({ results: [] });
  });

  it("won't recover through a recovery whose holder is no longer a member", async () => {
    const { hid, owner } = await linkedHousehold();
    const other = await newDevice();
    await addMember(hid, owner.device, other);
    const verifier = nonce();
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    // The holder gone, its recovery left behind, as an older bug or a race could leave it.
    await env.DB.prepare("UPDATE members SET removed = 1, removed_epoch = 1 WHERE household = ? AND device = ?")
      .bind(hid, owner.device.id)
      .run();

    const newPc = await signIn(undefined, owner.account);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier })).status).toBe(404);
    expect(await isMember(hid, other.id)).toBe(true);
  });

  it("refuses another verifier, a malformed one, and an account with nothing to recover", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    const newPc = await signIn(undefined, owner.account);
    const other = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));

    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier: other })).status).toBe(403);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", {})).status).toBe(400);
    expect((await asAccount(newPc, "POST", "/v1/account/recover", { verifier: base64urlEncode(new Uint8Array(16)) })).status).toBe(400);
    expect(await isMember(hid, newPc.device.id)).toBe(false);

    const lonely = await signIn();
    expect((await asAccount(lonely, "POST", "/v1/account/recover", { verifier: other })).status).toBe(404);
  });
});

describe("removing a member", () => {
  it("leaves the removed PC's session and its account's link alone, whoever removes it", async () => {
    const { hid, owner } = await linkedHousehold();
    const laptop = await signIn(undefined, owner.account);
    const desktop = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, laptop.device);
    await addMember(hid, owner.device, desktop.device);

    expect((await signedFetch(owner.device, "DELETE", `/v1/households/${hid}/members/${laptop.device.id}`)).status).toBe(200);
    expect((await signedFetch(desktop.device, "DELETE", `/v1/households/${hid}/members/${desktop.device.id}`)).status).toBe(200);

    for (const pc of [laptop, desktop, owner]) {
      expect((await asAccount(pc, "GET", "/v1/account/recovery")).status).toBe(404);
    }
    expect(await env.DB.prepare("SELECT household FROM account_households WHERE account = ?").bind(owner.account).first())
      .toEqual({ household: hid });
  });

  it("deletes a recovery only when the removed PC is its holder", async () => {
    const { hid, owner } = await linkedHousehold();
    const verifier = base64urlEncode(crypto.getRandomValues(new Uint8Array(32)));
    // A second account, a family member's, linked to the same household, holding its own recovery.
    const family = await signIn();
    await addMember(hid, owner.device, family.device);
    await asAccount(family, "POST", "/v1/account/household", { householdId: hid });
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    await asAccount(family, "PUT", "/v1/account/recovery", { body: envelope(), verifier, epoch: 1, replace: true });
    const other = await signIn(undefined, owner.account);
    await addMember(hid, owner.device, other.device);

    expect((await signedFetch(family.device, "DELETE", `/v1/households/${hid}/members/${other.device.id}`)).status).toBe(200);
    for (const account of [owner.account, family.account]) {
      expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(account).first(), account).not.toBeNull();
    }

    expect((await signedFetch(family.device, "DELETE", `/v1/households/${hid}/members/${owner.device.id}`)).status).toBe(200);
    expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(owner.account).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM recovery WHERE account = ?").bind(family.account).first()).not.toBeNull();
    expect(await env.DB.prepare("SELECT household FROM account_households WHERE account = ?").bind(owner.account).first())
      .toEqual({ household: hid });
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

  it("clears the requests the removed PC committed to, so their PCs can ask again, keeping those it approved", async () => {
    const { hid, owner } = await linkedHousehold();
    const second = await newDevice();
    await addMember(hid, owner.device, second);
    const approved = await signIn(undefined, owner.account);
    await asAccount(approved, "POST", "/v1/account/requests");
    await readyToApprove(hid, second, approved);
    expect((await approveAs(hid, second, approved)).status).toBe(200);
    const laptop = await signIn(undefined, owner.account);
    await asAccount(laptop, "POST", "/v1/account/requests");
    expect((await commitAs(hid, second, laptop)).status).toBe(200);

    expect((await signedFetch(owner.device, "DELETE", `/v1/households/${hid}/members/${second.id}`)).status).toBe(200);

    expect(await ownRequests(laptop)).toEqual([]);
    expect((await asAccount(laptop, "POST", "/v1/account/requests")).status).toBe(200);
    expect(await ownRequests(approved)).toMatchObject([{ approved: { epoch: 1 } }]);
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
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)), epoch: 1, replace: true });
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
    await asAccount(owner, "PUT", "/v1/account/recovery", { body: envelope(), verifier: base64urlEncode(new Uint8Array(32)), epoch: 1, replace: true });
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
