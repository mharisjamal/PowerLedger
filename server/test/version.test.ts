import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import worker from "../src/index";
import { utcDateString } from "../src/day";
import { handleHistory } from "../src/history";
import { handleReport } from "../src/report";
import { compareVersions, minVersion, parseVersion } from "../src/version";
import { gzipJson, historyBody, HOUR_MS, randomInstallId, randomKey } from "./support";
import validFull from "./fixtures/valid-full.json";

const strict = (minimum: string): Cloudflare.Env => ({ ...env, MIN_APP_VERSION: minimum });

function hourAgo(hoursAgo: number): number {
  return Math.floor(Date.now() / HOUR_MS) * HOUR_MS - hoursAgo * HOUR_MS;
}

function report(app: string) {
  return { ...structuredClone(validFull), installId: randomInstallId(), day: utcDateString(-1, new Date()), app };
}

async function upload(path: string, body: unknown, version?: string): Promise<Request> {
  return new Request(`https://example.com${path}`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${randomKey()}`,
      "Content-Encoding": "gzip",
      ...(version ? { "X-PowerLedger-Version": version } : {}),
    },
    body: await gzipJson(body),
  });
}

async function storedAnything(installId: string): Promise<boolean> {
  const row = await env.DB.prepare(
    `SELECT (SELECT COUNT(*) FROM installs WHERE id = ?1) + (SELECT COUNT(*) FROM reports WHERE install_id = ?1)
       + (SELECT COUNT(*) FROM histories WHERE install_id = ?1) AS n`,
  )
    .bind(installId)
    .first<{ n: number }>();
  return row!.n > 0;
}

describe("versions", () => {
  it("parse X.Y.Z, with or without a build or pre-release suffix", () => {
    expect(parseVersion("0.9.0")).toEqual([0, 9, 0]);
    expect(parseVersion("10.20.300+abc123")).toEqual([10, 20, 300]);
    expect(parseVersion("1.2.3-beta.1")).toEqual([1, 2, 3]);
    for (const bad of ["", "1.2", "1.2.3.4", "v1.2.3", "1.2.x", " 1.2.3"]) expect(parseVersion(bad)).toBeNull();
  });

  it("compare part by part, as numbers", () => {
    expect(compareVersions([0, 10, 0], [0, 9, 9])).toBeGreaterThan(0);
    expect(compareVersions([0, 9, 0], [0, 9, 0])).toBe(0);
    expect(compareVersions([0, 8, 99], [0, 9, 0])).toBeLessThan(0);
  });

  it("take the minimum from MIN_APP_VERSION, and 0.0.0 when it's empty or not a version", () => {
    expect(minVersion(strict("0.9.0"))).toBe("0.9.0");
    expect(minVersion(strict(""))).toBe("0.0.0");
    expect(minVersion(strict("soon"))).toBe("0.0.0");
    expect(minVersion(env)).toBe("0.0.0");
  });
});

describe("GET /v1/app-policy", () => {
  it("gives the minimum version, with no auth, cacheable for 5 minutes", async () => {
    const response = await SELF.fetch("https://example.com/v1/app-policy");

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ minVersion: "0.0.0" });
    expect(response.headers.get("Cache-Control")).toBe("public, max-age=300");
  });

  it("gives the minimum set in MIN_APP_VERSION", async () => {
    const response = await worker.fetch(new Request("https://example.com/v1/app-policy"), strict("0.9.0"));
    expect(await response.json()).toEqual({ minVersion: "0.9.0" });
  });
});

describe("the minimum version on /v1/report", () => {
  it("gives 426 for a header below the minimum, and stores nothing", async () => {
    const body = report("0.9.0");
    const response = await handleReport(await upload("/v1/report", body, "0.8.1"), strict("0.9.0"));

    expect(response.status).toBe(426);
    expect(await response.json()).toEqual({ error: "update required", minVersion: "0.9.0" });
    expect(await storedAnything(body.installId)).toBe(false);
  });

  it("gives 426 for a body's app below the minimum, whatever the header says", async () => {
    const body = report("0.8.1");
    const response = await handleReport(await upload("/v1/report", body, "0.9.0"), strict("0.9.0"));

    expect(response.status).toBe(426);
    expect(await storedAnything(body.installId)).toBe(false);
  });

  it("judges by the body's app when there's no header", async () => {
    const old = report("0.8.1");
    expect((await handleReport(await upload("/v1/report", old), strict("0.9.0"))).status).toBe(426);
    expect(await storedAnything(old.installId)).toBe(false);

    const current = report("0.9.0");
    expect((await handleReport(await upload("/v1/report", current), strict("0.9.0"))).status).toBe(200);
  });

  it("takes a version at or above the minimum, a build suffix on the header included", async () => {
    const body = report("0.10.0");
    expect((await handleReport(await upload("/v1/report", body, "0.10.0+abc"), strict("0.9.0"))).status).toBe(200);
  });

  it("gives 400 for a header that isn't a version", async () => {
    const response = await handleReport(await upload("/v1/report", report("0.9.0"), "latest"), strict("0.9.0"));
    expect(response.status).toBe(400);
  });
});

describe("the minimum version on /v1/history", () => {
  it("gives 426 for a header or a body's app below the minimum, and stores nothing", async () => {
    const byHeader = historyBody(randomInstallId(), hourAgo(50), 2);
    const headerResponse = await handleHistory(await upload("/v1/history", byHeader, "0.8.1"), strict("0.9.0"));
    expect(headerResponse.status).toBe(426);
    expect(await headerResponse.json()).toEqual({ error: "update required", minVersion: "0.9.0" });
    expect(await storedAnything(byHeader.installId)).toBe(false);

    const byBody = { ...historyBody(randomInstallId(), hourAgo(50), 2), app: "0.8.1" };
    expect((await handleHistory(await upload("/v1/history", byBody), strict("0.9.0"))).status).toBe(426);
    expect(await storedAnything(byBody.installId)).toBe(false);
  });

  it("takes the minimum itself", async () => {
    const body = historyBody(randomInstallId(), hourAgo(50), 2);
    expect((await handleHistory(await upload("/v1/history", body, "0.9.0"), strict("0.9.0"))).status).toBe(200);
  });
});

describe("the minimum version elsewhere", () => {
  it("never stops a delete or a consent, so an old app can still take its data back", async () => {
    const installId = randomInstallId();
    const key = randomKey();
    const post = (path: string, body: unknown) =>
      worker.fetch(
        new Request(`https://example.com${path}`, {
          method: "POST",
          headers: { Authorization: `Bearer ${key}`, "X-PowerLedger-Version": "0.1.0" },
          body: JSON.stringify(body),
        }),
        strict("0.9.0"),
      );

    const consent = { version: 1, diagnostics: false, usage: false, power: true, share: false };
    expect((await post("/v1/consent", { installId, consent })).status).toBe(200);
    expect((await post("/v1/delete", { installId })).status).toBe(200);
  });
});
