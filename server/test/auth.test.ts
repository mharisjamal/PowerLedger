import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import { bearer, checkInstall, countRequest, sha256hex } from "../src/auth";
import { randomInstallId, randomKey } from "./support";

describe("sha256hex", () => {
  it("matches the known hash of an empty string", async () => {
    expect(await sha256hex("")).toBe(
      "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    );
  });
});

describe("bearer", () => {
  const key = randomKey();

  it("reads a well-formed bearer key", () => {
    const request = new Request("https://example.com", { headers: { Authorization: `Bearer ${key}` } });
    expect(bearer(request)).toBe(key);
  });

  it("is null with no Authorization header", () => {
    expect(bearer(new Request("https://example.com"))).toBeNull();
  });

  it("is null without the Bearer scheme", () => {
    const request = new Request("https://example.com", { headers: { Authorization: key } });
    expect(bearer(request)).toBeNull();
  });

  it("is null when the key is the wrong length", () => {
    const request = new Request("https://example.com", { headers: { Authorization: "Bearer tooshort" } });
    expect(bearer(request)).toBeNull();
  });

  it("is null when the key has characters outside base64url", () => {
    const request = new Request("https://example.com", {
      headers: { Authorization: `Bearer ${"*".repeat(43)}` },
    });
    expect(bearer(request)).toBeNull();
  });
});

describe("checkInstall", () => {
  it("is new for an id never seen before, and records the key", async () => {
    const id = randomInstallId();
    expect(await checkInstall(env, id, randomKey())).toBe("new");
  });

  it("is ok for the same id and key again", async () => {
    const id = randomInstallId();
    const key = randomKey();
    await checkInstall(env, id, key);

    expect(await checkInstall(env, id, key)).toBe("ok");
  });

  it("is mismatch for the same id with a different key", async () => {
    const id = randomInstallId();
    await checkInstall(env, id, randomKey());

    expect(await checkInstall(env, id, randomKey())).toBe("mismatch");
  });

  it("is gone for a tombstoned id", async () => {
    const id = randomInstallId();
    await env.DB.prepare("INSERT INTO tombstones (id, deleted_at) VALUES (?, ?)").bind(id, Date.now()).run();

    expect(await checkInstall(env, id, randomKey())).toBe("gone");
  });
});

describe("countRequest", () => {
  it("starts at one and increments per call", async () => {
    const id = randomInstallId();

    expect(await countRequest(env, id)).toBe(1);
    expect(await countRequest(env, id)).toBe(2);
    expect(await countRequest(env, id)).toBe(3);
  });

  it("counts different installs separately", async () => {
    const a = randomInstallId();
    const b = randomInstallId();

    await countRequest(env, a);
    await countRequest(env, a);

    expect(await countRequest(env, b)).toBe(1);
  });

  it("counts reports, history and consent or delete each on their own", async () => {
    const id = randomInstallId();

    expect(await countRequest(env, id, "history")).toBe(1);
    expect(await countRequest(env, id)).toBe(1);
    expect(await countRequest(env, id)).toBe(2);
    expect(await countRequest(env, id, "control")).toBe(1);
    expect(await countRequest(env, id, "history")).toBe(2);
  });
});
