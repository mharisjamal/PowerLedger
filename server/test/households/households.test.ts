import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { handleHouseholdRoutes } from "../../src/households/routes";
import {
  addMember,
  aliasedDevice,
  compressedSpki,
  createHousehold,
  hookBefore,
  joinProof,
  newDevice,
  randomHouseholdId,
  signedFetch,
  signedRequest,
  type TestDevice,
} from "./support";

interface MemberJson {
  device: string;
  sign: string;
  dh: string;
  added: number;
  removed: number | null;
  addedEpoch: number;
  removedEpoch: number | null;
}

async function members(hid: string, by: TestDevice): Promise<MemberJson[]> {
  const response = await signedFetch(by, "GET", `/v1/households/${hid}/members`);
  expect(response.status).toBe(200);
  return ((await response.json()) as { members: MemberJson[] }).members;
}

function envelope(): string {
  // Stands for WrapFor's output: nonce, a 32-byte key and the tag.
  const bytes = crypto.getRandomValues(new Uint8Array(60));
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** `by` moves the household's key on to `epoch`, sealed to `to`. */
async function rotate(hid: string, by: TestDevice, epoch: number, to: TestDevice[] = [by]): Promise<void> {
  const response = await signedFetch(by, "POST", `/v1/households/${hid}/keys`, {
    epoch,
    envelopes: to.map((pc) => ({ device: pc.id, body: envelope() })),
  });
  expect(response.status).toBe(200);
}

describe("GET /v1/households/{hid}/members", () => {
  it("gives each member with the household epochs it was added and removed at, as {members}", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    const response = await signedFetch(first, "GET", `/v1/households/${hid}/members`);
    const body = (await response.json()) as { members: MemberJson[] };
    expect(body.members.find((member) => member.device === first.id)).toEqual({
      device: first.id,
      sign: first.sign,
      dh: first.dh,
      added: expect.any(Number),
      removed: null,
      addedEpoch: 1,
      removedEpoch: null,
    });

    await rotate(hid, first, 2, [first, second]);
    await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`);
    const removed = (await members(hid, first)).find((member) => member.device === second.id)!;
    expect(removed).toMatchObject({ addedEpoch: 1, removedEpoch: 2, removed: expect.any(Number) });

    await rotate(hid, first, 3);
    await addMember(hid, first, second);
    const back = (await members(hid, first)).find((member) => member.device === second.id)!;
    expect(back).toMatchObject({ addedEpoch: 3, removedEpoch: null, removed: null });
  });
});

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

  it("gives 404 for a PC that was never a member, and 200 for one already removed", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);

    const stranger = await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${(await newDevice()).id}`);
    expect(stranger.status).toBe(404);

    expect((await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`)).status).toBe(200);
    expect((await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`)).status).toBe(200);
  });

  it("ends the household with its last member, keeping it and its members as removed, so former members get 410", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    await signedFetch(first, "POST", `/v1/households/${hid}/batches`, {
      device: first.id,
      epoch: 1,
      seq: 1,
      body: envelope(),
      sig: btoa(String.fromCharCode(...new Uint8Array(64))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, ""),
    });

    expect((await signedFetch(second, "DELETE", `/v1/households/${hid}/members/${second.id}`)).status).toBe(200);
    expect((await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${first.id}`)).status).toBe(200);

    expect(await env.DB.prepare("SELECT 1 FROM households WHERE id = ?").bind(hid).first()).not.toBeNull();
    const rows = await env.DB.prepare("SELECT removed_epoch FROM members WHERE household = ?").bind(hid).all();
    expect(rows.results).toEqual([{ removed_epoch: 1 }, { removed_epoch: 1 }]);
    expect(await env.DB.prepare("SELECT 1 FROM batches WHERE household = ?").bind(hid).first()).toBeNull();

    for (const pc of [first, second]) {
      const response = await signedFetch(pc, "GET", `/v1/households/${hid}/members`);
      expect(response.status).toBe(410);
    }
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
    await rotate(hid, first, 2);
    await addMember(hid, first, second);

    expect((await signedFetch(second, "GET", `/v1/households/${hid}/keys/2`)).status).toBe(404);
    expect((await signedFetch(first, "GET", `/v1/households/${hid}/keys/3`)).status).toBe(404);
  });

  it("takes new keys only sealed to every current member, and to no other, as the members are when the epoch moves", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const joining = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    const post = (hook: () => Promise<unknown>) => async () => {
      const body = JSON.stringify({ epoch: 2, envelopes: [first, second].map((pc) => ({ device: pc.id, body: envelope() })) });
      const request = await signedRequest(first, "POST", `/v1/households/${hid}/keys`, body);
      return (await handleHouseholdRoutes(request, hookBefore(env, /INSERT INTO key_envelopes|UPDATE households SET epoch/, hook)))!.status;
    };
    const epoch = async () => (await env.DB.prepare("SELECT epoch FROM households WHERE id = ?").bind(hid).first<{ epoch: number }>())!.epoch;

    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, { epoch: 2, envelopes: [{ device: first.id, body: envelope() }] })).status).toBe(409);

    expect(await post(() => addMember(hid, second, joining))()).toBe(409);
    expect(await epoch()).toBe(1);
    await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${joining.id}`);

    expect(await post(() => signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`))()).toBe(409);
    expect(await epoch()).toBe(1);
    expect(await env.DB.prepare("SELECT 1 FROM key_envelopes WHERE household = ? AND epoch = 2").bind(hid).first()).toBeNull();
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

  it("answers every 409 with the household's epoch as it is then, which a PC that lost its epoch rotates on from", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const joining = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    const keys = (epoch: number, to: TestDevice[] = [first, second]) => ({
      epoch,
      envelopes: to.map((pc) => ({ device: pc.id, body: envelope() })),
    });
    const conflict = async (response: Response, epoch: number) => {
      expect(response.status).toBe(409);
      expect(await response.json()).toEqual({ error: expect.any(String), epoch });
    };
    const raced = async (epoch: number, hook: () => Promise<unknown>) => {
      const request = await signedRequest(first, "POST", `/v1/households/${hid}/keys`, JSON.stringify(keys(epoch)));
      return (await handleHouseholdRoutes(request, hookBefore(env, /INSERT INTO key_envelopes/, hook)))!;
    };

    await conflict(await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys(1)), 1);
    await conflict(await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys(3)), 1);
    await conflict(await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys(2, [first])), 1);

    // Another member takes epoch 2 between this post's look and its write: the answer has 2, not the 1 it looked at.
    await conflict(await raced(2, () => rotate(hid, second, 2, [first, second])), 2);
    await conflict(await raced(3, () => addMember(hid, second, joining)), 2);

    // Envelopes already there at the next epoch.
    await env.DB.prepare("INSERT INTO key_envelopes (household, epoch, device, from_device, body, created) VALUES (?, 3, ?, ?, ?, 1)")
      .bind(hid, joining.id, second.id, envelope())
      .run();
    await conflict(await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys(3, [first, second, joining])), 2);
  });

  it("takes an identical retry of the current epoch's keys from the same PC as done, and anything else as 409", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    const keys = { epoch: 2, envelopes: [{ device: first.id, body: envelope() }, { device: second.id, body: envelope() }] };

    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys)).status).toBe(200);
    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys)).status).toBe(200);   // its answer was lost
    expect((await signedFetch(second, "POST", `/v1/households/${hid}/keys`, keys)).status).toBe(409);  // not the sealer
    const changed = { epoch: 2, envelopes: [{ device: first.id, body: envelope() }, { device: second.id, body: envelope() }] };
    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, changed)).status).toBe(409);
    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, { epoch: 2, envelopes: [keys.envelopes[0]] })).status).toBe(409);
  });

  it("takes the identical re-post as done even after the members changed, or another member sealed a key at that epoch", async () => {
    const first = await newDevice();
    const second = await newDevice();
    const third = await newDevice();
    const hid = await createHousehold(first);
    await addMember(hid, first, second);
    const keys = { epoch: 2, envelopes: [{ device: first.id, body: envelope() }, { device: second.id, body: envelope() }] };
    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys)).status).toBe(200);

    // Before first re-posts the same bytes: second adds third with a key sealed at epoch 2, then leaves.
    await addMember(hid, second, third);
    await env.DB.prepare("INSERT INTO key_envelopes (household, epoch, device, from_device, body, created) VALUES (?, 2, ?, ?, ?, 1)")
      .bind(hid, third.id, second.id, envelope())
      .run();
    await signedFetch(second, "DELETE", `/v1/households/${hid}/members/${second.id}`);

    expect((await signedFetch(first, "POST", `/v1/households/${hid}/keys`, keys)).status).toBe(200);
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
      { epoch: 2, envelopes: [{ device: first.id, body: "A".repeat(1028) }] },
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
