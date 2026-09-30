import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { handleAeroRequest, handleAeroStatus } from "../src/aero";
import { randomAddress } from "./support";

const ADMIN = { Authorization: "Bearer test-admin-token" };
const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** A fresh AERO-XXXXX id, so tests never collide over shared D1 state. */
function randomAeroId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(5));
  return `AERO-${[...bytes].map((byte) => CROCKFORD[byte % 32]).join("")}`;
}

function request(id: unknown, name: unknown = "Study PC"): Promise<Response> {
  return SELF.fetch("https://example.com/v1/aero/request", {
    method: "POST",
    headers: { "CF-Connecting-IP": randomAddress() },
    body: JSON.stringify({ id, name }),
  });
}

async function status(id: string): Promise<string> {
  const response = await SELF.fetch(`https://example.com/v1/aero/status?id=${id}`, {
    headers: { "CF-Connecting-IP": randomAddress() },
  });
  expect(response.status).toBe(200);
  return ((await response.json()) as { state: string }).state;
}

function admin(action: "approve" | "revoke", id: string, headers: Record<string, string> = ADMIN): Promise<Response> {
  return SELF.fetch(`https://example.com/v1/admin/aero/${action}`, { method: "POST", headers, body: JSON.stringify({ id }) });
}

const refusing = { ...env, ADDRESS_LIMIT: { limit: async () => ({ success: false }) } } as Cloudflare.Env;

describe("POST /v1/aero/request", () => {
  it("records a request as pending, and asking again changes nothing", async () => {
    const id = randomAeroId();
    const first = await request(id, "Study PC");
    expect(first.status).toBe(200);
    expect(await first.json()).toEqual({ state: "pending" });

    const again = await request(id, "Another name");
    expect(await again.json()).toEqual({ state: "pending" });

    const row = await env.DB.prepare("SELECT name FROM aero_requests WHERE id = ?").bind(id).first<{ name: string }>();
    expect(row?.name).toBe("Study PC");
  });

  it.each([
    ["no prefix", "ABCDE12345"],
    ["lower case", "aero-abcde"],
    ["a letter Crockford leaves out", "AERO-ABCDU"],
    ["too long", "AERO-ABCDEF"],
    ["a number", 12345],
  ])("gives 400 for an id with %s", async (_why, id) => {
    expect((await request(id)).status).toBe(400);
  });

  it.each([
    ["null", null],
    ["empty", "   "],
    ["too long", "x".repeat(65)],
    ["not text", 42],
  ])("gives 400 for a name that is %s", async (_why, name) => {
    expect((await request(randomAeroId(), name)).status).toBe(400);
  });

  it("takes a 64-character name, with control characters stripped", async () => {
    const id = randomAeroId();
    expect((await request(id, `‮${"n".repeat(64)}\u0007`)).status).toBe(200);
    const row = await env.DB.prepare("SELECT name FROM aero_requests WHERE id = ?").bind(id).first<{ name: string }>();
    expect(row?.name).toBe("n".repeat(64));
  });

  it("gives 400 for a body that isn't JSON", async () => {
    const response = await SELF.fetch("https://example.com/v1/aero/request", { method: "POST", body: "not json" });
    expect(response.status).toBe(400);
  });

  it("gives 429 over the address limit", async () => {
    const response = await handleAeroRequest(
      new Request("https://example.com/v1/aero/request", { method: "POST", body: JSON.stringify({ id: randomAeroId(), name: "PC" }) }),
      refusing,
    );
    expect(response.status).toBe(429);
  });
});

describe("GET /v1/aero/status", () => {
  it("is none for an id never requested", async () => {
    expect(await status(randomAeroId())).toBe("none");
  });

  it("follows the owner: pending, approved, revoked, and approved again", async () => {
    const id = randomAeroId();
    await request(id);
    expect(await status(id)).toBe("pending");

    expect((await admin("approve", id)).status).toBe(200);
    expect(await status(id)).toBe("approved");

    const revoked = await admin("revoke", id);
    expect(await revoked.json()).toEqual({ id, state: "revoked" });
    expect(await status(id)).toBe("revoked");

    await admin("approve", id);
    expect(await status(id)).toBe("approved");
  });

  it("gives 400 for a malformed id, and 429 over the address limit", async () => {
    const bad = await SELF.fetch("https://example.com/v1/aero/status?id=nope");
    expect(bad.status).toBe(400);
    const limited = await handleAeroStatus(new Request(`https://example.com/v1/aero/status?id=${randomAeroId()}`), refusing);
    expect(limited.status).toBe(429);
  });
});

describe("the owner's /v1/admin/aero", () => {
  it("gives 401 without the admin token, on every route", async () => {
    const id = randomAeroId();
    await request(id);
    expect((await SELF.fetch("https://example.com/v1/admin/aero")).status).toBe(401);
    expect((await SELF.fetch("https://example.com/v1/admin/aero", { headers: { Authorization: "Bearer wrong" } })).status).toBe(401);
    expect((await admin("approve", id, {})).status).toBe(401);
    expect((await admin("revoke", id, { Authorization: "Bearer wrong" })).status).toBe(401);
    expect(await status(id)).toBe("pending");
  });

  it("lists requests with their names and states", async () => {
    const pending = randomAeroId();
    const approved = randomAeroId();
    await request(pending, "Office PC");
    await request(approved, "Gaming PC");
    await admin("approve", approved);

    const response = await SELF.fetch("https://example.com/v1/admin/aero", { headers: ADMIN });
    expect(response.status).toBe(200);
    const { items } = (await response.json()) as { items: { id: string; name: string; state: string; approvedAt: number | null }[] };
    expect(items).toContainEqual(expect.objectContaining({ id: pending, name: "Office PC", state: "pending", approvedAt: null }));
    expect(items).toContainEqual(expect.objectContaining({ id: approved, name: "Gaming PC", state: "approved" }));
  });

  it("gives 404 approving or revoking an id never requested, and 400 for a malformed one", async () => {
    expect((await admin("approve", randomAeroId())).status).toBe(404);
    expect((await admin("revoke", randomAeroId())).status).toBe(404);
    expect((await admin("approve", "nope")).status).toBe(400);
  });

  it("gives 404 for any other admin aero route", async () => {
    const response = await SELF.fetch("https://example.com/v1/admin/aero/delete", { method: "POST", headers: ADMIN, body: "{}" });
    expect(response.status).toBe(404);
  });
});
