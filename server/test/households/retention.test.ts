import { createExecutionContext, createScheduledController, env, waitOnExecutionContext } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { utcDateString } from "../../src/day";
import worker from "../../src/index";
import { runHouseholdRetention } from "../../src/households/retention";
import { runRetention } from "../../src/retention";
import { putBody } from "../../src/store";
import { withoutR2 } from "../support";
import { randomHouseholdId } from "./support";

const DAY_MS = 24 * 60 * 60 * 1000;
const MINUTE_MS = 60 * 1000;

async function seedBatch(target: Cloudflare.Env, household: string, seq: number, received: number): Promise<string> {
  const key = `batches/v1/${household}/${crypto.randomUUID()}`;
  await putBody(target, key, new Uint8Array(28), { contentType: "application/octet-stream", receivedAt: received });
  await env.DB.prepare(
    `INSERT INTO batches (household, seq, device, epoch, device_seq, bytes, received, r2_key)
     VALUES (?, ?, 'd', 1, ?, 28, ?, ?)`,
  )
    .bind(household, seq, seq, received, key)
    .run();
  return key;
}

async function batchSeqs(household: string): Promise<number[]> {
  const rows = await env.DB.prepare("SELECT seq FROM batches WHERE household = ? ORDER BY seq").bind(household).all<{ seq: number }>();
  return rows.results.map((row) => row.seq);
}

describe("household retention", () => {
  it("deletes batches and their bodies past 90 days, keeping newer ones", async () => {
    const now = Date.now();
    const hid = randomHouseholdId();
    const oldKey = await seedBatch(env, hid, 1, now - 91 * DAY_MS);
    const newKey = await seedBatch(env, hid, 2, now - 89 * DAY_MS);

    const controller = createScheduledController();
    const ctx = createExecutionContext();
    await worker.scheduled!(controller, env, ctx);
    await waitOnExecutionContext(ctx);

    expect(await batchSeqs(hid)).toEqual([2]);
    expect(await env.REPORTS!.get(oldKey)).toBeNull();
    expect(await env.REPORTS!.get(newKey)).not.toBeNull();
  });

  it("deletes an old batch's body kept in D1 while R2 isn't bound", async () => {
    const now = new Date();
    const noR2 = withoutR2(env);
    const hid = randomHouseholdId();
    const oldKey = await seedBatch(noR2, hid, 1, now.getTime() - 91 * DAY_MS);

    await runRetention(noR2, now);

    expect(await batchSeqs(hid)).toEqual([]);
    expect(await env.DB.prepare("SELECT 1 FROM report_bodies WHERE r2_key = ?").bind(oldKey).first()).toBeNull();
  });

  it("deletes meetings past 10 minutes, keeping live ones", async () => {
    const now = new Date();
    const ended = randomHouseholdId();
    const live = randomHouseholdId();
    await env.DB.prepare("INSERT INTO meetings (id, slot, body, created) VALUES (?, 'adder', x'00', ?), (?, 'adder', x'00', ?)")
      .bind(ended, now.getTime() - 11 * MINUTE_MS, live, now.getTime() - 9 * MINUTE_MS)
      .run();

    await runRetention(env, now);

    expect(await env.DB.prepare("SELECT 1 FROM meetings WHERE id = ?").bind(ended).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM meetings WHERE id = ?").bind(live).first()).not.toBeNull();
  });

  it("deletes per-PC request counts past 2 days, and seen signatures past 10 minutes", async () => {
    const now = new Date();
    const device = randomHouseholdId();
    await env.DB.prepare("INSERT INTO device_requests (device, utc_day, count) VALUES (?, ?, 5), (?, ?, 7)")
      .bind(device, utcDateString(-3, now), device, utcDateString(0, now))
      .run();
    await env.DB.prepare("INSERT INTO seen_signatures (device, r, seen) VALUES (?, 'old', ?), (?, 'new', ?)")
      .bind(device, now.getTime() - 11 * MINUTE_MS, device, now.getTime() - 9 * MINUTE_MS)
      .run();

    await runRetention(env, now);

    const counts = await env.DB.prepare("SELECT count FROM device_requests WHERE device = ?").bind(device).all();
    expect(counts.results).toEqual([{ count: 7 }]);
    const seen = await env.DB.prepare("SELECT r FROM seen_signatures WHERE device = ?").bind(device).all();
    expect(seen.results).toEqual([{ r: "new" }]);
  });
});

describe("household retention's budget", () => {
  it("works through old batches round after round, with no cap on rounds, while there's time", async () => {
    const now = new Date();
    const hid = randomHouseholdId();
    for (let seq = 1; seq <= 25; seq++) await seedBatch(env, hid, seq, now.getTime() - 91 * DAY_MS);

    await runHouseholdRetention(env, now, { batchSize: 2 });

    expect(await batchSeqs(hid)).toEqual([]);
  });

  it("stops at its time budget, leaving the rest for the next run", async () => {
    const now = new Date();
    const hid = randomHouseholdId();
    for (let seq = 1; seq <= 6; seq++) await seedBatch(env, hid, seq, now.getTime() - 91 * DAY_MS);
    const times = [0, 0, 25_000];
    const clock = () => times.shift() ?? 25_000;

    await runHouseholdRetention(env, now, { batchSize: 2, budgetMs: 20_000, clock });

    expect(await batchSeqs(hid)).toHaveLength(4);
  });
});

describe("retention with a part failing", () => {
  const brokenR2 = {
    ...env,
    REPORTS: {
      delete: async () => {
        throw new Error("R2 is down");
      },
    },
  } as unknown as Cloudflare.Env;

  it("still does the other parts when the batches' part fails", async () => {
    const now = new Date();
    const hid = randomHouseholdId();
    await seedBatch(env, hid, 1, now.getTime() - 91 * DAY_MS);
    const ended = randomHouseholdId();
    await env.DB.prepare("INSERT INTO meetings (id, slot, body, created) VALUES (?, 'adder', x'00', ?)")
      .bind(ended, now.getTime() - 11 * MINUTE_MS)
      .run();

    await runRetention(brokenR2, now);

    expect(await batchSeqs(hid)).toEqual([1]);
    expect(await env.DB.prepare("SELECT 1 FROM meetings WHERE id = ?").bind(ended).first()).toBeNull();
  });

  it("still clears request counts and household data when the reports' part fails", async () => {
    const now = new Date();
    const install = crypto.randomUUID();
    await env.DB.prepare(
      `INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key)
       VALUES (?, '2020-01-01', 1, 3, 'power', 'XX', ?)`,
    )
      .bind(install, `reports/v1/${install}/2020-01-01.json.gz`)
      .run();
    await env.DB.prepare("INSERT INTO requests (install_id, utc_day, count) VALUES (?, ?, 1)")
      .bind(install, utcDateString(-5, now))
      .run();
    const device = randomHouseholdId();
    await env.DB.prepare("INSERT INTO device_requests (device, utc_day, count) VALUES (?, ?, 1)")
      .bind(device, utcDateString(-5, now))
      .run();

    await runRetention(brokenR2, now);

    expect(await env.DB.prepare("SELECT 1 FROM reports WHERE install_id = ?").bind(install).first()).not.toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM requests WHERE install_id = ?").bind(install).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM device_requests WHERE device = ?").bind(device).first()).toBeNull();
  });
});
