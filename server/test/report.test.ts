import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { utcDateString } from "../src/day";
import { discardIfDeleted } from "../src/report";
import { gzip, gzipJson, randomInstallId, randomKey } from "./support";
import validFull from "./fixtures/valid-full.json";
import validDiagnosticsOnly from "./fixtures/valid-diagnostics-only.json";
import validLaptopPowerOnly from "./fixtures/valid-laptop-power-only.json";
import invalidSchemaNoSection from "./fixtures/invalid-schema-no-section.json";
import invalidSchemaSectionWithoutConsent from "./fixtures/invalid-schema-section-without-consent.json";
import invalidSchemaShareWithoutPower from "./fixtures/invalid-schema-share-without-power.json";
import invalidSchemaUnknownField from "./fixtures/invalid-schema-unknown-field.json";
import invalidMinutesNotRising from "./fixtures/invalid-minutes-not-rising.json";
import invalidMinutesNullLoad from "./fixtures/invalid-minutes-null-load.json";
import invalidMinutesOutOfRange from "./fixtures/invalid-minutes-out-of-range.json";
import invalidMinutesUnequalLengths from "./fixtures/invalid-minutes-unequal-lengths.json";

type Report = Record<string, unknown> & { installId: string; day: string };

// Yesterday, local-UTC: always inside the 15-days-back..tomorrow window regardless of when the
// suite runs, and always a *complete* day (the collector only ever sends finished days).
function freshReport(fixture: unknown, day = utcDateString(-1, new Date())): Report {
  const clone = structuredClone(fixture) as Report;
  clone.installId = randomInstallId();
  clone.day = day;
  return clone;
}

async function post(body: Uint8Array, key: string, extraHeaders: Record<string, string> = {}): Promise<Response> {
  return SELF.fetch("https://example.com/v1/report", {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip", ...extraHeaders },
    body,
  });
}

async function postReport(report: Report, key: string): Promise<Response> {
  return post(await gzipJson(report), key);
}

describe("POST /v1/report", () => {
  it.each([
    ["valid-full.json", validFull],
    ["valid-diagnostics-only.json", validDiagnosticsOnly],
    ["valid-laptop-power-only.json", validLaptopPowerOnly],
  ])("accepts %s, storing the bytes as sent and a D1 row", async (_name, fixture) => {
    const report = freshReport(fixture);
    const body = await gzipJson(report);

    const response = await post(body, randomKey());

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true });

    const object = await env.REPORTS!.get(`reports/v1/${report.installId}/${report.day}.json.gz`);
    expect(object).not.toBeNull();
    expect(new Uint8Array(await object!.arrayBuffer())).toEqual(body);

    const row = await env.DB.prepare("SELECT * FROM reports WHERE install_id = ? AND day = ?")
      .bind(report.installId, report.day)
      .first();
    expect(row).not.toBeNull();
  });

  it.each([
    ["invalid-schema-no-section.json", invalidSchemaNoSection],
    ["invalid-schema-section-without-consent.json", invalidSchemaSectionWithoutConsent],
    ["invalid-schema-share-without-power.json", invalidSchemaShareWithoutPower],
    ["invalid-schema-unknown-field.json", invalidSchemaUnknownField],
    ["invalid-minutes-not-rising.json", invalidMinutesNotRising],
    ["invalid-minutes-null-load.json", invalidMinutesNullLoad],
    ["invalid-minutes-out-of-range.json", invalidMinutesOutOfRange],
    ["invalid-minutes-unequal-lengths.json", invalidMinutesUnequalLengths],
  ])("refuses %s with 400", async (_name, fixture) => {
    const response = await postReport(freshReport(fixture), randomKey());
    expect(response.status).toBe(400);
  });

  it("gives 401 with no bearer key", async () => {
    const response = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { "Content-Encoding": "gzip" },
      body: await gzipJson(freshReport(validFull)),
    });
    expect(response.status).toBe(401);
  });

  it("gives 413 for a body over 1 MB, before it even tries to decompress", async () => {
    // Never valid gzip, but the size check runs first, so that never matters.
    const tooBig = new Uint8Array(1_048_577);
    const response = await post(tooBig, randomKey());
    expect(response.status).toBe(413);
  });

  it("gives 413 for a zip bomb", async () => {
    const bomb = await gzip(new Uint8Array(10 * 1024 * 1024));
    const response = await post(bomb, randomKey());
    expect(response.status).toBe(413);
  });

  it("gives 400 for bytes that aren't gzip at all, once past the size check", async () => {
    const notGzip = new TextEncoder().encode("hello, this is not gzip");
    const response = await post(notGzip, randomKey());
    expect(response.status).toBe(400);
  });

  it("gives 403 when the same install sends a different key", async () => {
    const report = freshReport(validFull);

    const first = await postReport(report, randomKey());
    expect(first.status).toBe(200);

    const second = await postReport({ ...report, day: utcDateString(-2, new Date()) }, randomKey());
    expect(second.status).toBe(403);
  });

  it("gives 400 for a day 16 days old", async () => {
    const report = freshReport(validFull, utcDateString(-16, new Date()));
    const response = await postReport(report, randomKey());
    expect(response.status).toBe(400);
  });

  it("leaves one object and one row when a day is sent twice", async () => {
    const report = freshReport(validFull);
    const key = randomKey();

    expect((await postReport(report, key)).status).toBe(200);
    expect((await postReport(report, key)).status).toBe(200);

    const objects = await env.REPORTS!.list({ prefix: `reports/v1/${report.installId}/` });
    expect(objects.objects).toHaveLength(1);

    const count = await env.DB.prepare("SELECT COUNT(*) AS n FROM reports WHERE install_id = ?")
      .bind(report.installId)
      .first<{ n: number }>();
    expect(count?.n).toBe(1);
  });

  it("gives 429 on the 21st request of the day from one install", async () => {
    const report = freshReport(validFull);
    const key = randomKey();

    let last: Response | undefined;
    for (let i = 0; i < 21; i++) {
      last = await postReport(report, key);
    }

    expect(last!.status).toBe(429);
  });

  it("gives 410 for a tombstoned install", async () => {
    const report = freshReport(validFull);
    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, ?)")
      .bind(report.installId, Date.now())
      .run();

    const response = await postReport(report, randomKey());
    expect(response.status).toBe(410);
  });

  it("stores the request's country", async () => {
    const report = freshReport(validFull);
    const body = await gzipJson(report);

    const response = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${randomKey()}`, "Content-Encoding": "gzip" },
      body,
      cf: { country: "PK" },
    });

    expect(response.status).toBe(200);
    const row = await env.DB.prepare("SELECT country FROM reports WHERE install_id = ?")
      .bind(report.installId)
      .first<{ country: string }>();
    expect(row?.country).toBe("PK");
  });
});

/** A body of `total` zero bytes, streamed in 64 KB chunks with no declared length. */
function streamedZeros(total: number): ReadableStream<Uint8Array> {
  let sent = 0;
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (sent >= total) {
        controller.close();
        return;
      }
      const chunk = new Uint8Array(Math.min(64 * 1024, total - sent));
      sent += chunk.byteLength;
      controller.enqueue(chunk);
    },
  });
}

describe("POST /v1/report, limits and races", () => {
  it("refuses a body over 1 MB even when its length isn't declared", async () => {
    const response = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${randomKey()}`, "Content-Encoding": "gzip" },
      body: streamedZeros(2 * 1024 * 1024),
    });
    expect(response.status).toBe(413);
  });

  it("takes back a report stored for an install deleted while it was being stored", async () => {
    const installId = randomInstallId();
    const day = utcDateString(-1, new Date());
    const r2Key = `reports/v1/${installId}/${day}.json.gz`;
    await env.REPORTS!.put(r2Key, new Uint8Array([1, 2, 3]));
    await env.DB.prepare(
      "INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key) VALUES (?, ?, 0, 3, 'power', 'XX', ?)",
    )
      .bind(installId, day, r2Key)
      .run();

    expect(await discardIfDeleted(env, installId, day, r2Key)).toBe(false);
    expect(await env.REPORTS!.head(r2Key)).not.toBeNull();

    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, 0)").bind(installId).run();
    expect(await discardIfDeleted(env, installId, day, r2Key)).toBe(true);
    expect(await env.REPORTS!.head(r2Key)).toBeNull();
    const row = await env.DB.prepare("SELECT 1 FROM reports WHERE install_id = ?").bind(installId).first();
    expect(row).toBeNull();
  });
});
