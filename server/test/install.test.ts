import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { utcDateString } from "../src/day";
import { gzipJson, randomInstallId, randomKey } from "./support";
import validFull from "./fixtures/valid-full.json";

function consentBody(installId: string, overrides: Partial<Record<string, unknown>> = {}) {
  return {
    installId,
    consent: { version: 1, diagnostics: true, usage: false, power: true, share: false, ...overrides },
  };
}

async function postJson(path: string, body: unknown, key: string): Promise<Response> {
  return SELF.fetch(`https://example.com${path}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

describe("POST /v1/consent", () => {
  it("records the switches for a new install", async () => {
    const id = randomInstallId();
    const key = randomKey();

    const response = await postJson("/v1/consent", consentBody(id), key);

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true });

    const row = await env.DB.prepare("SELECT diagnostics, usage, power, share FROM installs WHERE id = ?")
      .bind(id)
      .first<{ diagnostics: number; usage: number; power: number; share: number }>();
    expect(row).toEqual({ diagnostics: 1, usage: 0, power: 1, share: 0 });
  });

  it("gives 400 for share without power", async () => {
    const response = await postJson(
      "/v1/consent",
      consentBody(randomInstallId(), { power: false, share: true }),
      randomKey(),
    );
    expect(response.status).toBe(400);
  });

  it("gives 401 with no bearer key", async () => {
    const response = await SELF.fetch("https://example.com/v1/consent", {
      method: "POST",
      body: JSON.stringify(consentBody(randomInstallId())),
    });
    expect(response.status).toBe(401);
  });

  it("gives 403 when the key doesn't match a known install", async () => {
    const id = randomInstallId();
    await postJson("/v1/consent", consentBody(id), randomKey());

    const response = await postJson("/v1/consent", consentBody(id), randomKey());
    expect(response.status).toBe(403);
  });

  it("gives 410 for a tombstoned install", async () => {
    const id = randomInstallId();
    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, ?)").bind(id, Date.now()).run();

    const response = await postJson("/v1/consent", consentBody(id), randomKey());
    expect(response.status).toBe(410);
  });

  it("gives 400 for a malformed installId", async () => {
    const response = await postJson("/v1/consent", consentBody("not-a-guid"), randomKey());
    expect(response.status).toBe(400);
  });
});

describe("POST /v1/delete", () => {
  it("removes the objects and rows, then a report gives 410", async () => {
    const id = randomInstallId();
    const key = randomKey();

    const report = { ...structuredClone(validFull), installId: id, day: utcDateString(-1, new Date()) };
    const reportResponse = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
      body: await gzipJson(report),
    });
    expect(reportResponse.status).toBe(200);

    const deleteResponse = await postJson("/v1/delete", { installId: id }, key);
    expect(deleteResponse.status).toBe(200);

    const objects = await env.REPORTS.list({ prefix: `reports/v1/${id}/` });
    expect(objects.objects).toHaveLength(0);

    const reportsRow = await env.DB.prepare("SELECT 1 FROM reports WHERE install_id = ?").bind(id).first();
    expect(reportsRow).toBeNull();
    const installRow = await env.DB.prepare("SELECT 1 FROM installs WHERE id = ?").bind(id).first();
    expect(installRow).toBeNull();
    const tombstoneRow = await env.DB.prepare("SELECT 1 FROM tombstones WHERE id = ?").bind(id).first();
    expect(tombstoneRow).not.toBeNull();

    const secondReport = await SELF.fetch("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${key}`, "Content-Encoding": "gzip" },
      body: await gzipJson({ ...structuredClone(validFull), installId: id, day: utcDateString(-2, new Date()) }),
    });
    expect(secondReport.status).toBe(410);
  });

  it("tombstones an install nobody has seen before, and gets 200", async () => {
    const id = randomInstallId();
    const response = await postJson("/v1/delete", { installId: id }, randomKey());

    expect(response.status).toBe(200);
    const tombstoneRow = await env.DB.prepare("SELECT 1 FROM tombstones WHERE id = ?").bind(id).first();
    expect(tombstoneRow).not.toBeNull();
  });

  it("gives 403 for a wrong key, and removes nothing", async () => {
    const id = randomInstallId();
    const key = randomKey();
    await postJson("/v1/consent", consentBody(id), key);

    const response = await postJson("/v1/delete", { installId: id }, randomKey());

    expect(response.status).toBe(403);
    const installRow = await env.DB.prepare("SELECT 1 FROM installs WHERE id = ?").bind(id).first();
    expect(installRow).not.toBeNull();
    const tombstoneRow = await env.DB.prepare("SELECT 1 FROM tombstones WHERE id = ?").bind(id).first();
    expect(tombstoneRow).toBeNull();
  });

  it("gives 410 for an already tombstoned install", async () => {
    const id = randomInstallId();
    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, ?)").bind(id, Date.now()).run();

    const response = await postJson("/v1/delete", { installId: id }, randomKey());
    expect(response.status).toBe(410);
  });
});

describe("consent and delete bodies", () => {
  it("refuses a consent body over 4 KB even when its length isn't declared", async () => {
    let sent = 0;
    const body = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (sent >= 64 * 1024) {
          controller.close();
          return;
        }
        sent += 1024;
        controller.enqueue(new Uint8Array(1024).fill(32));
      },
    });
    const response = await SELF.fetch("https://example.com/v1/consent", {
      method: "POST",
      headers: { Authorization: `Bearer ${randomKey()}` },
      body,
    });
    expect(response.status).toBe(413);
  });
});
