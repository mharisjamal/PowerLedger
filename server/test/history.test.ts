import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { utcDateString } from "../src/day";
import { handleHistory } from "../src/history";
import { handleReport } from "../src/report";
import { gzipJson, historyBody, HOUR_MS, randomAddress, randomInstallId, randomKey, withoutR2 } from "./support";
import validFull from "./fixtures/valid-full.json";

async function post(body: unknown, key: string, headers: Record<string, string> = {}): Promise<Response> {
  return SELF.fetch("https://example.com/v1/history", {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip", ...headers },
    body: await gzipJson(body),
  });
}

/** The start of the hour `hoursAgo` hours before now. */
function hourAgo(hoursAgo: number): number {
  return Math.floor(Date.now() / HOUR_MS) * HOUR_MS - hoursAgo * HOUR_MS;
}

describe("POST /v1/history", () => {
  it("stores a chunk as sent, indexes it by its first hour, and records the install's consent", async () => {
    const history = historyBody(randomInstallId(), hourAgo(100), 48);
    const body = await gzipJson(history);

    const response = await SELF.fetch("https://example.com/v1/history", {
      method: "POST",
      headers: { Authorization: `Bearer ${randomKey()}`, "Content-Encoding": "gzip" },
      body,
      cf: { country: "PK" },
    });

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true });

    const fromMs = history.hours[0].t;
    const object = await env.REPORTS!.get(`history/v1/${history.installId}/${fromMs}.json.gz`);
    expect(object).not.toBeNull();
    expect(new Uint8Array(await object!.arrayBuffer())).toEqual(body);

    const row = await env.DB.prepare("SELECT from_ms, to_ms, bytes, country, r2_key FROM histories WHERE install_id = ?")
      .bind(history.installId)
      .first();
    expect(row).toEqual({
      from_ms: fromMs,
      to_ms: fromMs + 48 * HOUR_MS,
      bytes: body.byteLength,
      country: "PK",
      r2_key: `history/v1/${history.installId}/${fromMs}.json.gz`,
    });

    const install = await env.DB.prepare("SELECT consent_version, power, app_version FROM installs WHERE id = ?")
      .bind(history.installId)
      .first();
    expect(install).toEqual({ consent_version: 2, power: 1, app_version: history.app });
  });

  it("takes the most a chunk may hold: 744 hours, 31 days", async () => {
    const response = await post(historyBody(randomInstallId(), hourAgo(2000), 744), randomKey());
    expect(response.status).toBe(200);
  });

  it("leaves one object and one row when a chunk is sent twice", async () => {
    const history = historyBody(randomInstallId(), hourAgo(300), 24);
    const key = randomKey();

    expect((await post(history, key)).status).toBe(200);
    expect((await post(history, key)).status).toBe(200);

    const objects = await env.REPORTS!.list({ prefix: `history/v1/${history.installId}/` });
    expect(objects.objects).toHaveLength(1);
    const count = await env.DB.prepare("SELECT COUNT(*) AS n FROM histories WHERE install_id = ?")
      .bind(history.installId)
      .first<{ n: number }>();
    expect(count?.n).toBe(1);
  });

  it.each([
    ["consent version 1", (h: ReturnType<typeof historyBody>) => { h.consent.version = 1; }],
    ["power off", (h: ReturnType<typeof historyBody>) => { h.consent.power = false; h.consent.share = false; }],
    ["another schema", (h: ReturnType<typeof historyBody>) => { (h as Record<string, unknown>).schema = 1; }],
    ["no hours", (h: ReturnType<typeof historyBody>) => { h.hours = []; }],
    ["an unknown field", (h: ReturnType<typeof historyBody>) => { (h as Record<string, unknown>).day = "2026-09-24"; }],
    ["an unknown hour column", (h: ReturnType<typeof historyBody>) => { (h.hours[1] as Record<string, unknown>).extraW = 1; }],
    ["a missing hour column", (h: ReturnType<typeof historyBody>) => { delete (h.hours[1] as Record<string, unknown>).gapS; }],
    ["an hour off the hour", (h: ReturnType<typeof historyBody>) => { h.hours[1].t += 60_000; }],
    ["hours out of order", (h: ReturnType<typeof historyBody>) => { h.hours[1].t = h.hours[0].t; }],
    ["a column out of range", (h: ReturnType<typeof historyBody>) => { h.hours[2].avgW = 6000; }],
    ["a column that isn't a number", (h: ReturnType<typeof historyBody>) => { (h.hours[2] as Record<string, unknown>).cpuWh = "1"; }],
    ["a count that isn't whole", (h: ReturnType<typeof historyBody>) => { h.hours[2].sampleCount = 1.5; }],
  ])("gives 400 for %s", async (_name, spoil) => {
    const history = historyBody(randomInstallId(), hourAgo(100), 4);
    spoil(history);
    const response = await post(history, randomKey());
    expect(response.status).toBe(400);
    expect(await env.DB.prepare("SELECT 1 FROM histories WHERE install_id = ?").bind(history.installId).first()).toBeNull();
  });

  it("gives 400 for an hour in the future", async () => {
    const response = await post(historyBody(randomInstallId(), hourAgo(-2), 1), randomKey());
    expect(response.status).toBe(400);
  });

  it("gives 400 for a chunk spanning more than 31 days", async () => {
    const history = historyBody(randomInstallId(), hourAgo(2000), 2);
    history.hours[1].t = history.hours[0].t + 744 * HOUR_MS;
    const response = await post(history, randomKey());
    expect(response.status).toBe(400);
  });

  it("gives 401 with no bearer key", async () => {
    const response = await SELF.fetch("https://example.com/v1/history", {
      method: "POST",
      headers: { "Content-Encoding": "gzip" },
      body: await gzipJson(historyBody(randomInstallId(), hourAgo(10), 1)),
    });
    expect(response.status).toBe(401);
  });

  it("gives 413 for a body that isn't gzip-encoded", async () => {
    const response = await SELF.fetch("https://example.com/v1/history", {
      method: "POST",
      headers: { Authorization: `Bearer ${randomKey()}` },
      body: JSON.stringify(historyBody(randomInstallId(), hourAgo(10), 1)),
    });
    expect(response.status).toBe(413);
  });

  it("gives 403 when the same install sends a different key", async () => {
    const history = historyBody(randomInstallId(), hourAgo(100), 2);
    expect((await post(history, randomKey())).status).toBe(200);
    expect((await post(history, randomKey())).status).toBe(403);
  });

  it("gives 410 for a tombstoned install", async () => {
    const history = historyBody(randomInstallId(), hourAgo(100), 2);
    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, ?)").bind(history.installId, Date.now()).run();
    expect((await post(history, randomKey())).status).toBe(410);
  });

  it("takes 60 chunks a day from one install, gives 429 on the 61st, and leaves the reports' own count alone", async () => {
    const installId = randomInstallId();
    const key = randomKey();

    for (let i = 0; i < 60; i++) {
      const chunk = historyBody(installId, hourAgo(1000 + i * 10), 2);
      expect((await post(chunk, key, { "CF-Connecting-IP": randomAddress() })).status).toBe(200);
    }
    const last = await post(historyBody(installId, hourAgo(5000), 2), key, { "CF-Connecting-IP": randomAddress() });
    expect(last.status).toBe(429);

    const report = { ...structuredClone(validFull), installId, day: utcDateString(-1, new Date()) };
    const reported = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip", "CF-Connecting-IP": randomAddress() },
      body: await gzipJson(report),
    });
    expect(reported.status).toBe(200);
  }, 60_000);
});

describe("POST /v1/history, without R2 bound", () => {
  it("stores the body in D1 and indexes it", async () => {
    const history = historyBody(randomInstallId(), hourAgo(100), 3);
    const body = await gzipJson(history);

    const response = await handleHistory(
      new Request("https://example.com/v1/history", {
        method: "POST",
        headers: { Authorization: `Bearer ${randomKey()}`, "Content-Encoding": "gzip" },
        body,
      }),
      withoutR2(env),
    );

    expect(response.status).toBe(200);
    const r2Key = `history/v1/${history.installId}/${history.hours[0].t}.json.gz`;
    expect(await env.REPORTS!.get(r2Key)).toBeNull();
    const stored = await env.DB.prepare("SELECT body FROM report_bodies WHERE r2_key = ?").bind(r2Key).first<{ body: ArrayBuffer }>();
    expect(new Uint8Array(stored!.body)).toEqual(body);
  });
});

describe("POST /v1/delete, with history", () => {
  it("deletes the install's history with its reports, in R2 and in D1", async () => {
    const installId = randomInstallId();
    const key = randomKey();
    const inR2 = historyBody(installId, hourAgo(200), 2);
    const inD1 = historyBody(installId, hourAgo(100), 2);

    expect((await post(inR2, key)).status).toBe(200);
    const d1Response = await handleHistory(
      new Request("https://example.com/v1/history", {
        method: "POST",
        headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
        body: await gzipJson(inD1),
      }),
      withoutR2(env),
    );
    expect(d1Response.status).toBe(200);
    const report = { ...structuredClone(validFull), installId, day: utcDateString(-1, new Date()) };
    const reported = await handleReport(
      new Request("https://example.com/v1/report", {
        method: "POST",
        headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
        body: await gzipJson(report),
      }),
      env,
    );
    expect(reported.status).toBe(200);

    const deleted = await SELF.fetch("https://example.com/v1/delete", {
      method: "POST",
      headers: { Authorization: `Bearer ${key}` },
      body: JSON.stringify({ installId }),
    });
    expect(deleted.status).toBe(200);

    expect((await env.REPORTS!.list({ prefix: `history/v1/${installId}/` })).objects).toHaveLength(0);
    expect(
      await env.DB.prepare("SELECT 1 FROM report_bodies WHERE r2_key LIKE ?").bind(`history/v1/${installId}/%`).first(),
    ).toBeNull();
    expect(await env.DB.prepare("SELECT 1 FROM histories WHERE install_id = ?").bind(installId).first()).toBeNull();
    expect((await post(historyBody(installId, hourAgo(300), 2), key)).status).toBe(410);
  });
});
