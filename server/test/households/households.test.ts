import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import {
  addMember,
  aliasedDevice,
  compressedSpki,
  createHousehold,
  joinProof,
  newDevice,
  randomHouseholdId,
  signedFetch,
  type TestDevice,
} from "./support";

interface MemberJson {
  device: string;
  sign: string;
  dh: string;
  added: number;
  removed: number | null;
}

async function members(hid: string, by: TestDevice): Promise<MemberJson[]> {
  const response = await signedFetch(by, "GET", `/v1/households/${hid}/members`);
  expect(response.status).toBe(200);
  return response.json();
}

function envelope(): string {
  // Stands for WrapFor's output: nonce, a 32-byte key and the tag.
  const bytes = crypto.getRandomValues(new Uint8Array(60));
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

describe("POST /v1/households", () => {
  it("creates a household with its creator as the one member", async () => {
    const pc = await newDevice();
    const hid = await createHousehold(pc);

    const list = await members(hid, pc);
    expect(list).toHaveLength(1);
    expect(list[0]).toMatchObject({ device: pc.id, sign: pc.sign, dh: pc.dh, removed: null });
    expect(list[0].added).toBeGreaterThan(Date.now() - 60_000);
  });

  it("takes the same creation again, from the same PC, as done", async () => {
    const pc = await newDevice();
    const hid = await createHousehold(pc);

    const again = await signedFetch(pc, "POST", "/v1/households", { id: hid, sign: pc.sign, dh: pc.dh });
    expect(again.status).toBe(200);
    expect(await members(hid, pc)).toHaveLength(1);
  });

  it("gives 409 for an ID another PC already made", async () => {
    const hid = await createHousehold(await newDevice());
    const other = await newDevice();

    const response = await signedFetch(other, "POST", "/v1/households", { id: hid, sign: other.sign, dh: other.dh });
    expect(response.status).toBe(409);
  });

  it("gives 401 when the request isn't signed by the posted key", async () => {
    const pc = await newDevice();
    const other = await newDevice();

    const response = await signedFetch(pc, "POST", "/v1/households", { id: randomHouseholdId(), sign: other.sign, dh: other.dh });
    expect(response.status).toBe(401);
  });

  it("gives 400 for a malformed ID or keys", async () => {
    const pc = await newDevice();
    for (const body of [
      { id: "ABC", sign: pc.sign, dh: pc.dh },
      { id: randomHouseholdId(), sign: "not-a-key", dh: pc.dh },
      { id: randomHouseholdId(), sign: pc.sign, dh: pc.sign.slice(0, 40) },
      { id: randomHouseholdId(), sign: pc.sign },
    ]) {
      const response = await signedFetch(pc, "POST", "/v1/households", body);
      expect(response.status, JSON.stringify(body)).toBe(400);
    }
  });

  it("gives 400 for a key in a compressed or other aliased encoding, which would give one key two device IDs", async () => {
    const pc = await newDevice();
    const aliased = await aliasedDevice(pc);

    const aliasedSign = await signedFetch(aliased, "POST", "/v1/households", { id: randomHouseholdId(), sign: aliased.sign, dh: pc.dh });
    expect(aliasedSign.status).toBe(400);

    const aliasedDh = await signedFetch(pc, "POST", "/v1/households", { id: randomHouseholdId(), sign: pc.sign, dh: compressedSpki(pc.dh) });
    expect(aliasedDh.status).toBe(400);
  });

  it("gives 413 for a body over 16 KB", async () => {
    const pc = await newDevice();
    const response = await signedFetch(pc, "POST", "/v1/households", { id: randomHouseholdId(), sign: pc.sign, dh: pc.dh, pad: "x".repeat(17_000) });
    expect(response.status).toBe(413);
  });
});

describe("POST /v1/households/{hid}/members", () => {
  it("lets a member add a PC, which is then a member too", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);

    await addMember(hid, first, second);

    const list = await members(hid, second);
    expect(list.map((member) => member.device).sort()).toEqual([first.id, second.id].sort());
    expect(list.find((member) => member.device === second.id)).toMatchObject({ sign: second.sign, dh: second.dh, removed: null });
  });

  it("gives 401 to a PC that isn't a member", async () => {
    const hid = await createHousehold(await newDevice());
    const stranger = await newDevice();

    const response = await signedFetch(stranger, "POST", `/v1/households/${hid}/members`, { sign: stranger.sign, dh: stranger.dh });
    expect(response.status).toBe(401);
  });

  it("takes adding a current member again with the same keys as done, and with another dh key as 409", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);

    await addMember(hid, first, second);
    expect(await members(hid, first)).toHaveLength(2);

    const otherDh = (await newDevice()).dh;
    const response = await signedFetch(first, "POST", `/v1/households/${hid}/members`, {
      sign: second.sign,
      dh: otherDh,
      proof: await joinProof(second, hid, second.sign, otherDh),
    });
    expect(response.status).toBe(409);
    expect((await members(hid, first)).find((member) => member.device === second.id)?.dh).toBe(second.dh);
  });

  it("needs the joining PC's proof: its signature over this household and the keys posted", async () => {
    const first = await newDevice();
    const joining = await newDevice();
    const hid = await createHousehold(first);
    const add = (body: Record<string, unknown>) => signedFetch(first, "POST", `/v1/households/${hid}/members`, body);
    const keys = { sign: joining.sign, dh: joining.dh };

    expect((await add(keys)).status).toBe(400);
    expect((await add({ ...keys, proof: "not a proof" })).status).toBe(400);
    expect((await add({ ...keys, proof: await joinProof(first, hid, joining.sign, joining.dh) })).status).toBe(403);
    expect((await add({ ...keys, proof: await joinProof(joining, randomHouseholdId()) })).status).toBe(403);
    expect((await add({ ...keys, proof: await joinProof(joining, hid, joining.sign, (await newDevice()).dh) })).status).toBe(403);
    expect(await members(hid, first)).toHaveLength(1);

    expect((await add({ ...keys, proof: await joinProof(joining, hid) })).status).toBe(200);
  });

  it("stops at 16 current members", async () => {
    const first = await newDevice();
    const hid = await createHousehold(first);
    for (let i = 0; i < 15; i++) await addMember(hid, first, await newDevice());

    const seventeenth = await newDevice();
    const response = await signedFetch(first, "POST", `/v1/households/${hid}/members`, {
      sign: seventeenth.sign,
      dh: seventeenth.dh,
      proof: await joinProof(seventeenth, hid),
    });
    expect(response.status).toBe(409);
    expect(await members(hid, first)).toHaveLength(16);
  });

  it("gives 400 for keys that aren't P-256 public keys", async () => {
    const first = await newDevice();
    const hid = await createHousehold(first);

    const response = await signedFetch(first, "POST", `/v1/households/${hid}/members`, { sign: "AAAA", dh: first.dh });
    expect(response.status).toBe(400);
  });
});

describe("DELETE /v1/households/{hid}/members/{device}", () => {
  it("lets a member remove another, who is then refused", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);

    const response = await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`);
    expect(response.status).toBe(200);

    const removed = (await members(hid, first)).find((member) => member.device === second.id);
    expect(removed?.removed).toBeGreaterThan(Date.now() - 60_000);
    expect((await signedFetch(second, "GET", `/v1/households/${hid}/members`)).status).toBe(410);
  });

  it("lets a member leave, and a removed PC be added back", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);

    expect((await signedFetch(second, "DELETE", `/v1/households/${hid}/members/${second.id}`)).status).toBe(200);
    expect((await signedFetch(second, "GET", `/v1/households/${hid}/members`)).status).toBe(410);

    await addMember(hid, first, second);
    const back = (await members(hid, second)).find((member) => member.device === second.id);
    expect(back?.removed).toBeNull();
  });

  it("gives 404 for a PC that isn't a current member", async () => {
    const first = await newDevice();
    const hid = await createHousehold(first);

    const response = await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${(await newDevice()).id}`);
    expect(response.status).toBe(404);
  });

  it("ends the household when its last member leaves", async () => {
    const first = await newDevice();
    const hid = await createHousehold(first);

    expect((await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${first.id}`)).status).toBe(200);

    expect(await env.DB.prepare("SELECT 1 FROM households WHERE id = ?").bind(hid).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM members WHERE household = ?").bind(hid).first()).toBeNull();
  });
});

describe("the household's keys", () => {
  it("keeps one envelope per member, each fetching only its own, with who sealed it", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    const forFirst = envelope();
    const forSecond = envelope();

    const posted = await signedFetch(first, "POST", `/v1/households/${hid}/keys`, {
      epoch: 2,
      envelopes: [
        { device: first.id, body: forFirst },
        { device: second.id, body: forSecond },
      ],
    });
    expect(posted.status).toBe(200);

    const mine = await signedFetch(second, "GET", `/v1/households/${hid}/keys/2`);
    expect(mine.status).toBe(200);
    expect(await mine.json()).toEqual({ epoch: 2, from: first.id, body: forSecond });

    const theirs = await signedFetch(first, "GET", `/v1/households/${hid}/keys/2`);
    expect(await theirs.json()).toEqual({ epoch: 2, from: first.id, body: forFirst });
  });

  it("gives 404 when there's no envelope for this PC at that epoch", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    await signedFetch(first, "POST", `/v1/households/${hid}/keys`, { epoch: 2, envelopes: [{ device: first.id, body: envelope() }] });

    expect((await signedFetch(second, "GET", `/v1/households/${hid}/keys/2`)).status).toBe(404);
    expect((await signedFetch(first, "GET", `/v1/households/${hid}/keys/3`)).status).toBe(404);
  });

  it("takes only the household's next epoch, current + 1, which then becomes current", async () => {
    const first = await newDevice();
    const hid = await createHousehold(first);
    const keysAt = (epoch: number) => ({ epoch, envelopes: [{ device: first.id, body: envelope() }] });
    const post = (epoch: number) => signedFetch(first, "POST", `/v1/households/${hid}/keys`, keysAt(epoch));
    const current = async () =>
      (await env.DB.prepare("SELECT epoch FROM households WHERE id = ?").bind(hid).first<{ epoch: number }>())!.epoch;

    expect(await current()).toBe(1);
    expect((await post(1)).status).toBe(409);
    expect((await post(3)).status).toBe(409);
    expect((await post(2)).status).toBe(200);
    expect(await current()).toBe(2);

    expect((await post(2)).status).toBe(409);
    expect((await post(3)).status).toBe(200);
    expect(await current()).toBe(3);
  });

  it("refuses envelopes for PCs that aren't current members, and malformed ones", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`);

    for (const body of [
      { epoch: 2, envelopes: [{ device: second.id, body: envelope() }] },
      { epoch: 2, envelopes: [{ device: (await newDevice()).id, body: envelope() }] },
      { epoch: 2, envelopes: [] },
      { epoch: -1, envelopes: [{ device: first.id, body: envelope() }] },
      { epoch: 2, envelopes: [{ device: first.id, body: "not base64url!" }] },
      { epoch: 2, envelopes: [{ device: first.id, body: envelope() }, { device: first.id, body: envelope() }] },
    ]) {
      const response = await signedFetch(first, "POST", `/v1/households/${hid}/keys`, body);
      expect(response.status, JSON.stringify(body)).toBe(400);
    }
  });

  it("gives 401 to a PC that isn't a member", async () => {
    const hid = await createHousehold(await newDevice());
    const stranger = await newDevice();

    expect((await signedFetch(stranger, "GET", `/v1/households/${hid}/keys/1`)).status).toBe(401);
    const posted = await signedFetch(stranger, "POST", `/v1/households/${hid}/keys`, { epoch: 2, envelopes: [{ device: stranger.id, body: envelope() }] });
    expect(posted.status).toBe(401);
  });
});

describe("GET /v1/households/{hid}/members", () => {
  it("gives 401 to a PC that isn't a member, and to an unsigned request", async () => {
    const hid = await createHousehold(await newDevice());

    expect((await signedFetch(await newDevice(), "GET", `/v1/households/${hid}/members`)).status).toBe(401);
    expect((await SELF.fetch(`https://example.com/v1/households/${hid}/members`)).status).toBe(401);
  });
});
