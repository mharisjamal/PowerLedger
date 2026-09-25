import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { adminAuthorized, handleAdmin } from "../src/admin";
import { utcDateString } from "../src/day";
import { handleReport } from "../src/report";
import { gzipJson, randomInstallId, randomKey, withoutR2 } from "./support";
import validFull from "./fixtures/valid-full.json";

const ADMIN = { Authorization: "Bearer test-admin-token" };

async function sendReport(installId: string, day: string, key = randomKey()): Promise<Response> {
  const report = { ...structuredClone(validFull), installId, day };
  return SELF.fetch("https://example.com/v1/report", {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
    body: await gzipJson(report),
  });
}

async function setShare(installId: string, key: string, share: boolean): Promise<void> {
  await SELF.fetch("https://example.com/v1/consent", {
    method: "POST",
    headers: { Authorization: `Bearer ${key}` },
    body: JSON.stringify({
      installId,
      consent: { version: 1, diagnostics: false, usage: false, power: true, share },
    }),
  });
}

describe("admin auth", () => {
  it("gives 401 with no token, on every admin route", async () => {
    for (const path of ["/admin/stats", "/admin/list", "/admin/object?key=reports/v1/x/2026-09-24.json.gz"]) {
      const response = await SELF.fetch(`https://example.com${path}`);
      expect(response.status).toBe(401);
    }
  });

  it("gives 401 with the wrong token", async () => {
    const response = await SELF.fetch("https://example.com/admin/stats", {
      headers: { Authorization: "Bearer not-the-token" },
    });
    expect(response.status).toBe(401);
  });

  it("refuses everything when ADMIN_TOKEN is empty or missing", () => {
    expect(adminAuthorized(new Request("https://example.com", { headers: ADMIN }), { ADMIN_TOKEN: "" } as never)).toBe(
      false,
    );
    expect(
      adminAuthorized(new Request("https://example.com", { headers: ADMIN }), {} as never),
    ).toBe(false);
  });
});

describe("GET /admin/stats", () => {
  it("counts installs, switches and days", async () => {
    const id = randomInstallId();
    const key = randomKey();
    const day = utcDateString(-1, new Date());

    expect((await sendReport(id, day, key)).status).toBe(200);

    const response = await SELF.fetch("https://example.com/admin/stats", { headers: ADMIN });
    expect(response.status).toBe(200);
    const stats = await response.json<{
      installs: number;
      switches: { diagnostics: number; usage: number; power: number; share: number };
      days: { day: string; reports: number; bytes: number }[];
    }>();

    expect(stats.installs).toBeGreaterThanOrEqual(1);
    expect(stats.switches.power).toBeGreaterThanOrEqual(1);
    expect(stats.days.some((row) => row.day === day && row.reports >= 1)).toBe(true);
  });
});

describe("GET /admin/list", () => {
  it("pages with after, ordered by day then install", async () => {
    const day = utcDateString(-3, new Date());
    const ids = [randomInstallId(), randomInstallId(), randomInstallId()].sort();

    for (const id of ids) {
      expect((await sendReport(id, day)).status).toBe(200);
    }

    const first = await SELF.fetch(`https://example.com/admin/list?from=${day}&to=${day}&limit=2`, {
      headers: ADMIN,
    });
    const firstPage = await first.json<{ items: { installId: string }[]; next: string | null }>();
    expect(firstPage.items).toHaveLength(2);
    expect(firstPage.next).not.toBeNull();

    const second = await SELF.fetch(
      `https://example.com/admin/list?from=${day}&to=${day}&limit=2&after=${firstPage.next}`,
      { headers: ADMIN },
    );
    const secondPage = await second.json<{ items: { installId: string }[]; next: string | null }>();
    expect(secondPage.items.length).toBeGreaterThanOrEqual(1);

    const seen = [...firstPage.items, ...secondPage.items].map((item) => item.installId);
    for (const id of ids) expect(seen).toContain(id);
  });

  it("with shared=1, leaves out an install that hasn't shared", async () => {
    const day = utcDateString(-4, new Date());
    const sharingId = randomInstallId();
    const sharingKey = randomKey();
    const quietId = randomInstallId();
    const quietKey = randomKey();

    await sendReport(sharingId, day, sharingKey);
    await setShare(sharingId, sharingKey, true);
    await sendReport(quietId, day, quietKey);
    await setShare(quietId, quietKey, false);

    const response = await SELF.fetch(`https://example.com/admin/list?from=${day}&to=${day}&shared=1`, {
      headers: ADMIN,
    });
    const page = await response.json<{ items: { installId: string }[] }>();
    const ids = page.items.map((item) => item.installId);

    expect(ids).toContain(sharingId);
    expect(ids).not.toContain(quietId);
  });
});

describe("GET /admin/object", () => {
  it("returns the bytes exactly as stored", async () => {
    const id = randomInstallId();
    const day = utcDateString(-5, new Date());
    const key = randomKey();
    const report = { ...structuredClone(validFull), installId: id, day };
    const body = await gzipJson(report);

    const sent = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
      body,
    });
    expect(sent.status).toBe(200);

    const r2Key = `reports/v1/${id}/${day}.json.gz`;
    const response = await SELF.fetch(`https://example.com/admin/object?key=${r2Key}`, { headers: ADMIN });

    expect(response.status).toBe(200);
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(body);
  });

  it("gives 400 for a key outside reports/v1/", async () => {
    const response = await SELF.fetch("https://example.com/admin/object?key=../secrets.txt", { headers: ADMIN });
    expect(response.status).toBe(400);
  });

  it("gives 404 for a missing key", async () => {
    const response = await SELF.fetch("https://example.com/admin/object?key=reports/v1/nobody/2026-01-01.json.gz", {
      headers: ADMIN,
    });
    expect(response.status).toBe(404);
  });
});

describe("GET /admin/list, complete", () => {
  it("says whether each day is complete or today so far", async () => {
    const day = utcDateString(0, new Date());
    const partialId = randomInstallId();
    const wholeId = randomInstallId();
    const partial = { ...structuredClone(validFull), installId: partialId, day, complete: false };
    const sent = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${randomKey()}`, "Content-Encoding": "gzip" },
      body: await gzipJson(partial),
    });
    expect(sent.status).toBe(200);
    expect((await sendReport(wholeId, day)).status).toBe(200);

    const response = await SELF.fetch(`https://example.com/admin/list?from=${day}&to=${day}`, { headers: ADMIN });
    const page = await response.json<{ items: { installId: string; complete: boolean }[] }>();

    expect(page.items.find((item) => item.installId === partialId)?.complete).toBe(false);
    expect(page.items.find((item) => item.installId === wholeId)?.complete).toBe(true);
  });
});

describe("GET /admin/list, guards", () => {
  it("refuses a cursor that isn't a day and an install", async () => {
    for (const after of ["2026-09-01|not-a-guid", "yesterday|0f8fad5b-d9cb-469f-a165-70867728950e", "2026-09-01|"]) {
      const response = await SELF.fetch(`https://example.com/admin/list?after=${encodeURIComponent(after)}`, { headers: ADMIN });
      expect(response.status).toBe(400);
    }
  });

  it("never lists what a deleted install left behind, shared or not", async () => {
    const installId = randomInstallId();
    const day = utcDateString(-1, new Date());
    await env.DB.prepare(
      "INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key) VALUES (?, ?, 0, 3, 'power', 'XX', ?)",
    )
      .bind(installId, day, `reports/v1/${installId}/${day}.json.gz`)
      .run();
    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, 0)").bind(installId).run();

    const response = await SELF.fetch(`https://example.com/admin/list?from=${day}&to=${day}&limit=1000`, { headers: ADMIN });
    const listed = (await response.json()) as { items: { installId: string }[] };
    expect(listed.items.some((item) => item.installId === installId)).toBe(false);
  });
});

describe("GET /admin/object, without R2 bound", () => {
  it("serves a body stored in D1", async () => {
    const id = randomInstallId();
    const day = utcDateString(-6, new Date());
    const key = randomKey();
    const noR2 = withoutR2(env);
    const report = { ...structuredClone(validFull), installId: id, day };
    const body = await gzipJson(report);

    const sent = await handleReport(
      new Request("https://example.com/v1/report", {
        method: "POST",
        headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
        body,
      }),
      noR2,
    );
    expect(sent.status).toBe(200);

    const r2Key = `reports/v1/${id}/${day}.json.gz`;
    const response = await handleAdmin(
      new Request(`https://example.com/admin/object?key=${r2Key}`, { headers: ADMIN }),
      noR2,
    );

    expect(response.status).toBe(200);
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(body);
  });
});
