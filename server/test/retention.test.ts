import { createExecutionContext, createScheduledController, waitOnExecutionContext } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import worker from "../src/index";
import { utcDateString, utcDateYearsAgo } from "../src/day";
import { runRetention } from "../src/retention";
import { putBody } from "../src/store";
import { randomInstallId, withoutR2 } from "./support";

async function runScheduled(): Promise<void> {
  const controller = createScheduledController();
  const ctx = createExecutionContext();
  await worker.scheduled!(controller, env, ctx);
  await waitOnExecutionContext(ctx);
}

async function seedReport(installId: string, day: string): Promise<string> {
  const r2Key = `reports/v1/${installId}/${day}.json.gz`;
  await env.REPORTS!.put(r2Key, new Uint8Array([1, 2, 3]));
  await env.DB.prepare(
    `INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key)
     VALUES (?, ?, ?, 3, 'power', 'XX', ?)`,
  )
    .bind(installId, day, Date.now(), r2Key)
    .run();
  return r2Key;
}

describe("retention", () => {
  it("deletes reports and objects past 3 years, keeping recent ones", async () => {
    const now = new Date();
    const oldInstall = randomInstallId();
    const oldDay = utcDateYearsAgo(4, now);
    const oldKey = await seedReport(oldInstall, oldDay);

    const recentInstall = randomInstallId();
    const recentDay = utcDateString(-1, now);
    const recentKey = await seedReport(recentInstall, recentDay);

    await runScheduled();

    expect(await env.REPORTS!.get(oldKey)).toBeNull();
    expect(
      await env.DB.prepare("SELECT 1 FROM reports WHERE install_id = ?").bind(oldInstall).first(),
    ).toBeNull();

    expect(await env.REPORTS!.get(recentKey)).not.toBeNull();
    expect(
      await env.DB.prepare("SELECT 1 FROM reports WHERE install_id = ?").bind(recentInstall).first(),
    ).not.toBeNull();
  });

  it("deletes request counts older than 2 days, keeping today's", async () => {
    const now = new Date();
    const oldInstall = randomInstallId();
    const oldDay = utcDateString(-5, now);
    await env.DB.prepare("INSERT INTO requests (install_id, utc_day, count) VALUES (?, ?, 1)")
      .bind(oldInstall, oldDay)
      .run();

    const recentInstall = randomInstallId();
    const today = new Date().toISOString().slice(0, 10);
    await env.DB.prepare("INSERT INTO requests (install_id, utc_day, count) VALUES (?, ?, 1)")
      .bind(recentInstall, today)
      .run();

    await runScheduled();

    expect(
      await env.DB.prepare("SELECT 1 FROM requests WHERE install_id = ?").bind(oldInstall).first(),
    ).toBeNull();
    expect(
      await env.DB.prepare("SELECT 1 FROM requests WHERE install_id = ?").bind(recentInstall).first(),
    ).not.toBeNull();
  });
});

describe("retention, without R2 bound", () => {
  it("deletes an old body stored in D1", async () => {
    const now = new Date();
    const oldInstall = randomInstallId();
    const oldDay = utcDateYearsAgo(4, now);
    const r2Key = `reports/v1/${oldInstall}/${oldDay}.json.gz`;
    const noR2 = withoutR2(env);

    await putBody(noR2, r2Key, new Uint8Array([1, 2, 3]), { contentType: "application/json", receivedAt: Date.now() });
    await env.DB.prepare(
      `INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key)
       VALUES (?, ?, ?, 3, 'power', 'XX', ?)`,
    )
      .bind(oldInstall, oldDay, Date.now(), r2Key)
      .run();

    await runRetention(noR2, now);

    expect(await env.DB.prepare("SELECT 1 FROM report_bodies WHERE r2_key = ?").bind(r2Key).first()).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM reports WHERE install_id = ?").bind(oldInstall).first()).toBeNull();
  });
});
