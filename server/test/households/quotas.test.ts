import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { MAX_BATCH_BYTES_PER_DAY, MAX_BATCHES_PER_DAY, MAX_TOTAL_BATCH_BYTES_PER_DAY } from "../../src/households/batches";
import { base64urlEncode } from "../../src/households/encoding";
import { runRetention } from "../../src/retention";
import { utcDateString } from "../../src/day";
import { createHousehold, newDevice, signedFetch, type TestDevice } from "./support";

function today(): string {
  return new Date().toISOString().slice(0, 10);
}

function post(hid: string, by: TestDevice, seq: number, size = 64): Promise<Response> {
  const body = base64urlEncode(crypto.getRandomValues(new Uint8Array(size)));
  return signedFetch(by, "POST", `/v1/households/${hid}/batches`, { device: by.id, epoch: 1, seq, body });
}

/** Sets what `device` has already posted today (its create request made the day's row). */
async function alreadyPosted(device: string, batches: number, bytes: number): Promise<void> {
  await env.DB.prepare(
    `INSERT INTO device_requests (device, utc_day, count, batches, batch_bytes) VALUES (?, ?, 0, ?, ?)
     ON CONFLICT (device, utc_day) DO UPDATE SET batches = excluded.batches, batch_bytes = excluded.batch_bytes`,
  )
    .bind(device, today(), batches, bytes)
    .run();
}

async function usage(device: string): Promise<{ batches: number; batch_bytes: number } | null> {
  return env.DB.prepare("SELECT batches, batch_bytes FROM device_requests WHERE device = ? AND utc_day = ?")
    .bind(device, today())
    .first<{ batches: number; batch_bytes: number }>();
}

describe("a PC's daily batch quota", () => {
  it(`takes ${MAX_BATCHES_PER_DAY} batches a UTC day, and gives 429 past that`, async () => {
    const pc = await newDevice();
    const hid = await createHousehold(pc);
    await alreadyPosted(pc.id, MAX_BATCHES_PER_DAY - 1, 0);

    expect((await post(hid, pc, 1)).status).toBe(200);
    expect((await post(hid, pc, 2)).status).toBe(429);
    expect(await usage(pc.id)).toEqual({ batches: MAX_BATCHES_PER_DAY, batch_bytes: 64 });
  });

  it("takes 5 MB of sealed batches a UTC day, and gives 429 for one that would pass it", async () => {
    const pc = await newDevice();
    const hid = await createHousehold(pc);
    await alreadyPosted(pc.id, 0, MAX_BATCH_BYTES_PER_DAY - 100);

    expect((await post(hid, pc, 1, 64)).status).toBe(200);
    expect((await post(hid, pc, 2, 64)).status).toBe(429);
    expect(await usage(pc.id)).toEqual({ batches: 1, batch_bytes: MAX_BATCH_BYTES_PER_DAY - 36 });
  });
});

describe("the server's daily total", () => {
  it("takes 2 GB of batches a UTC day in all, then gives 503 without counting against the PC", async () => {
    const pc = await newDevice();
    const hid = await createHousehold(pc);
    await env.DB.prepare(
      `INSERT INTO daily_totals (utc_day, batch_bytes) VALUES (?, ?)
       ON CONFLICT (utc_day) DO UPDATE SET batch_bytes = excluded.batch_bytes`,
    )
      .bind(today(), MAX_TOTAL_BATCH_BYTES_PER_DAY - 100)
      .run();
    try {
      expect((await post(hid, pc, 1, 64)).status).toBe(200);
      const busy = await post(hid, pc, 2, 64);
      expect(busy.status).toBe(503);
      expect(await busy.json()).toEqual({ error: "The server is busy; try again later." });
      expect(await usage(pc.id)).toEqual({ batches: 1, batch_bytes: 64 });
    } finally {
      await env.DB.prepare("DELETE FROM daily_totals WHERE utc_day = ?").bind(today()).run();
    }
  });

  it("is cleared by the daily cron after 2 days", async () => {
    const now = new Date();
    await env.DB.prepare("INSERT INTO daily_totals (utc_day, batch_bytes) VALUES (?, 1), (?, 2)")
      .bind(utcDateString(-3, now), utcDateString(-1, now))
      .run();

    await runRetention(env, now);

    const left = await env.DB.prepare("SELECT utc_day FROM daily_totals ORDER BY utc_day").all<{ utc_day: string }>();
    expect(left.results.map((row) => row.utc_day)).toEqual([utcDateString(-1, now)]);
  });
});
