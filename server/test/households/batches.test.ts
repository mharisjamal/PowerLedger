import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { type MemberRow } from "../../src/households/auth";
import { handleGetBatches } from "../../src/households/batches";
import { base64urlEncode } from "../../src/households/encoding";
import { handleHouseholdRoutes } from "../../src/households/routes";
import { runRetention } from "../../src/retention";
import { withoutR2 } from "../support";
import { addMember, createHousehold, newDevice, signedFetch, signedRequest, type TestDevice } from "./support";

interface BatchPage {
  items: { seq: number; device: string; epoch: number; body: string; sig: string }[];
  next: number;
  more: boolean;
}

function sealed(size = 64): string {
  return base64urlEncode(crypto.getRandomValues(new Uint8Array(size)));
}

/** Stands for the sender's signature over BatchToSign: 64 bytes the Worker keeps and hands on, but never checks. */
function batchSig(): string {
  return sealed(64);
}

async function post(hid: string, by: TestDevice, seq: number, body = sealed(), epoch = 1, sig = batchSig()): Promise<Response> {
  return signedFetch(by, "POST", `/v1/households/${hid}/batches`, { device: by.id, epoch, seq, body, sig });
}

async function page(hid: string, by: TestDevice, query = "after=0&limit=100"): Promise<BatchPage> {
  const response = await signedFetch(by, "GET", `/v1/households/${hid}/batches?${query}`);
  expect(response.status).toBe(200);
  return response.json();
}

async function pair(): Promise<{ hid: string; first: TestDevice; second: TestDevice }> {
  const first = await newDevice();
  const second = await newDevice();
  const hid = await createHousehold(first);
  await addMember(hid, first, second);
  return { hid, first, second };
}

describe("POST and GET /v1/households/{hid}/batches", () => {
  it("gives another member each batch as posted, its signature too, with the sender's own seq, and the cursor as next", async () => {
    const { hid, first, second } = await pair();
    const body = sealed();
    const sig = batchSig();

    const posted = await post(hid, first, 17, body, 3, sig);
    expect(posted.status).toBe(200);

    const fetched = await page(hid, second);
    expect(fetched.items).toEqual([{ seq: 17, device: first.id, epoch: 3, body, sig }]);
    expect(fetched.more).toBe(false);

    const row = await env.DB.prepare("SELECT seq FROM batches WHERE household = ?").bind(hid).first<{ seq: number }>();
    expect(fetched.next).toBe(row!.seq);
    expect((await page(hid, second, `after=${fetched.next}`)).items).toEqual([]);
  });

  it("numbers a household's batches 1, 2, 3 in the order they arrive", async () => {
    const { hid, first, second } = await pair();
    await post(hid, first, 1);
    await post(hid, second, 1);
    await post(hid, first, 2);

    const rows = await env.DB.prepare("SELECT seq, device, device_seq FROM batches WHERE household = ? ORDER BY seq")
      .bind(hid)
      .all();
    expect(rows.results).toEqual([
      { seq: 1, device: first.id, device_seq: 1 },
      { seq: 2, device: second.id, device_seq: 1 },
      { seq: 3, device: first.id, device_seq: 2 },
    ]);
  });

  it("never numbers a batch again, even after retention has taken every batch the household had", async () => {
    const { hid, first, second } = await pair();
    await post(hid, first, 1);
    await post(hid, first, 2);
    await env.DB.prepare("UPDATE batches SET received = ? WHERE household = ?").bind(Date.now() - 91 * 24 * 3600 * 1000, hid).run();
    await runRetention(env, new Date());
    expect(await env.DB.prepare("SELECT 1 FROM batches WHERE household = ?").bind(hid).first()).toBeNull();

    await post(hid, first, 3);

    const row = await env.DB.prepare("SELECT seq FROM batches WHERE household = ?").bind(hid).first<{ seq: number }>();
    expect(row?.seq).toBe(3);
    expect((await page(hid, second, "after=2")).items.map((item) => item.seq)).toEqual([3]);
  });

  it("leaves out the caller's own batches", async () => {
    const { hid, first, second } = await pair();
    await post(hid, first, 1);
    await post(hid, second, 1);
    await post(hid, first, 2);

    const forSecond = await page(hid, second);
    expect(forSecond.items.map((item) => [item.device, item.seq])).toEqual([[first.id, 1], [first.id, 2]]);
    expect(forSecond.next).toBe(3);

    const forFirst = await page(hid, first);
    expect(forFirst.items.map((item) => [item.device, item.seq])).toEqual([[second.id, 1]]);
    expect(forFirst.next).toBe(2);
  });

  it("pages by limit, saying when more is waiting", async () => {
    const { hid, first, second } = await pair();
    for (let seq = 1; seq <= 3; seq++) await post(hid, first, seq);

    const one = await page(hid, second, "after=0&limit=2");
    expect(one.items.map((item) => item.seq)).toEqual([1, 2]);
    expect(one.more).toBe(true);

    const two = await page(hid, second, `after=${one.next}&limit=2`);
    expect(two.items.map((item) => item.seq)).toEqual([3]);
    expect(two.more).toBe(false);

    const none = await page(hid, second, `after=${two.next}&limit=2`);
    expect(none).toEqual({ items: [], next: two.next, more: false });
  });

  it("takes 100 at most, and after and limit as whole numbers only", async () => {
    const { hid, second } = await pair();
    expect((await page(hid, second, "limit=1000")).items).toEqual([]);
    expect((await page(hid, second, "")).items).toEqual([]);

    for (const query of ["after=-1", "after=x", "limit=1.5"]) {
      const response = await signedFetch(second, "GET", `/v1/households/${hid}/batches?${query}`);
      expect(response.status, query).toBe(400);
    }
  });

  it("gives 410 to a removed member, both ways", async () => {
    const { hid, first, second } = await pair();
    await signedFetch(first, "DELETE", `/v1/households/${hid}/members/${second.id}`);

    expect((await post(hid, second, 1)).status).toBe(410);
    expect((await signedFetch(second, "GET", `/v1/households/${hid}/batches?after=0`)).status).toBe(410);
  });

  it("gives 413 for a batch over 1 MB, and takes one just under", async () => {
    const { hid, first } = await pair();
    const sig = batchSig();
    const wrapper = JSON.stringify({ device: first.id, epoch: 1, seq: 1, body: "", sig }).length;
    const justUnder = "A".repeat(1_048_576 - wrapper);

    expect((await post(hid, first, 1, justUnder, 1, sig)).status).toBe(200);
    expect((await post(hid, first, 2, justUnder + "AAAA", 1, sig)).status).toBe(413);
  });

  it("gives 400 for a batch that isn't the sender's own or isn't well formed", async () => {
    const { hid, first, second } = await pair();
    const sig = batchSig();
    for (const body of [
      { device: second.id, epoch: 1, seq: 1, body: sealed(), sig },
      { device: first.id, epoch: "1", seq: 1, body: sealed(), sig },
      { device: first.id, epoch: 1, seq: -1, body: sealed(), sig },
      { device: first.id, epoch: 1, seq: 1, body: "not base64url!", sig },
      { device: first.id, epoch: 1, seq: 1, body: sealed(27), sig },
      { device: first.id, epoch: 1, seq: 1, sig },
      { device: first.id, epoch: 1, seq: 1, body: sealed() },
      { device: first.id, epoch: 1, seq: 1, body: sealed(), sig: sealed(63) },
      { device: first.id, epoch: 1, seq: 1, body: sealed(), sig: sealed(65) },
      { device: first.id, epoch: 1, seq: 1, body: sealed(), sig: "not base64url!" },
      { device: first.id, epoch: 1, seq: 1, body: sealed(), sig: 42 },
    ]) {
      const response = await signedFetch(first, "POST", `/v1/households/${hid}/batches`, body);
      expect(response.status, JSON.stringify(body)).toBe(400);
    }
  });

  it("keeps bodies in D1 while R2 isn't bound", async () => {
    const { hid, first, second } = await pair();
    const noR2 = withoutR2(env);
    const body = sealed();
    const sig = batchSig();

    const postRequest = await signedRequest(
      first, "POST", `/v1/households/${hid}/batches`, JSON.stringify({ device: first.id, epoch: 1, seq: 5, body, sig }),
    );
    expect((await handleHouseholdRoutes(postRequest, noR2))!.status).toBe(200);

    const row = await env.DB.prepare("SELECT r2_key FROM batches WHERE household = ?").bind(hid).first<{ r2_key: string }>();
    expect(await env.DB.prepare("SELECT 1 FROM report_bodies WHERE r2_key = ?").bind(row!.r2_key).first()).not.toBeNull();
    expect(await env.REPORTS!.get(row!.r2_key)).toBeNull();

    const getRequest = await signedRequest(second, "GET", `/v1/households/${hid}/batches?after=0`);
    const fetched = (await (await handleHouseholdRoutes(getRequest, noR2))!.json()) as BatchPage;
    expect(fetched.items).toEqual([{ seq: 5, device: first.id, epoch: 1, body, sig }]);
  });

  it("ends a page early when its bodies pass the page's size, with more still waiting", async () => {
    const { hid, first, second } = await pair();
    for (let seq = 1; seq <= 3; seq++) await post(hid, first, seq, sealed(1000));
    const member = await env.DB.prepare("SELECT * FROM members WHERE household = ? AND device = ?")
      .bind(hid, second.id)
      .first<MemberRow>();

    const url = new URL(`https://example.com/v1/households/${hid}/batches?after=0`);
    const fetched = (await (await handleGetBatches(env, member!, url, 2500)).json()) as BatchPage;
    expect(fetched.items.map((item) => item.seq)).toEqual([1, 2]);
    expect(fetched.more).toBe(true);
    expect(fetched.next).toBe(2);
  });

  it("reads a page's bodies from D1 in one query, not one an item", async () => {
    const { hid, first, second } = await pair();
    const noR2 = withoutR2(env);
    const bodies: string[] = [];
    for (let seq = 1; seq <= 30; seq++) {
      const body = sealed();
      bodies.push(body);
      const posted = JSON.stringify({ device: first.id, epoch: 1, seq, body, sig: batchSig() });
      const request = await signedRequest(first, "POST", `/v1/households/${hid}/batches`, posted);
      expect((await handleHouseholdRoutes(request, noR2))!.status).toBe(200);
    }
    const member = await env.DB.prepare("SELECT * FROM members WHERE household = ? AND device = ?")
      .bind(hid, second.id)
      .first<MemberRow>();
    const counted = countingQueries(noR2);

    const url = new URL(`https://example.com/v1/households/${hid}/batches?after=0`);
    const fetched = (await (await handleGetBatches(counted.env, member!, url)).json()) as BatchPage;

    expect(fetched.items.map((item) => item.body)).toEqual(bodies);
    expect(counted.queries()).toBeLessThanOrEqual(2);
  });
});

/** `target` with its D1 counting the statements prepared on it. */
function countingQueries(target: Cloudflare.Env): { env: Cloudflare.Env; queries: () => number } {
  let queries = 0;
  const db = target.DB;
  const counting = new Proxy(db, {
    get(object, property) {
      if (property === "prepare") {
        return (sql: string) => {
          queries++;
          return object.prepare(sql);
        };
      }
      const value = Reflect.get(object, property);
      return typeof value === "function" ? value.bind(object) : value;
    },
  });
  return { env: { ...target, DB: counting }, queries: () => queries };
}
