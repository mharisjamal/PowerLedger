import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { hex } from "../../src/households/encoding";
import { handleGetSlot, handlePutSlot, MEETING_LIFETIME_MS } from "../../src/households/meetings";
import { randomAddress } from "./support";

function meetingId(): string {
  return hex(crypto.getRandomValues(new Uint8Array(16)));
}

function put(id: string, slot: string, body: BodyInit): Promise<Response> {
  return SELF.fetch(`https://example.com/v1/meetings/${id}/${slot}`, {
    method: "PUT",
    headers: { "CF-Connecting-IP": randomAddress() },
    body,
  });
}

function get(id: string, slot: string): Promise<Response> {
  return SELF.fetch(`https://example.com/v1/meetings/${id}/${slot}`, { headers: { "CF-Connecting-IP": randomAddress() } });
}

function putRequest(id: string, slot: string, body: BodyInit): Request {
  return new Request(`https://example.com/v1/meetings/${id}/${slot}`, {
    method: "PUT",
    headers: { "CF-Connecting-IP": randomAddress() },
    body,
  });
}

const refusing = { limit: async () => ({ success: false }) };

async function liveMeetings(): Promise<number> {
  return (await env.DB.prepare("SELECT COUNT(DISTINCT id) AS n FROM meetings").first<{ n: number }>())!.n;
}

describe("meeting volume", () => {
  it("clears ended meetings on every put", async () => {
    const ended = meetingId();
    await env.DB.prepare("INSERT INTO meetings (id, slot, body, created) VALUES (?, 'adder', x'00', ?)")
      .bind(ended, Date.now() - MEETING_LIFETIME_MS - 1000)
      .run();

    expect((await put(meetingId(), "adder", "{}")).status).toBe(200);

    expect(await env.DB.prepare("SELECT 1 FROM meetings WHERE id = ?").bind(ended).first()).toBeNull();
  });

  it("puts starting a meeting behind a per-address limit of its own", async () => {
    const limited = { ...env, MEETING_LIMIT: refusing } as unknown as Cloudflare.Env;
    const started = meetingId();
    expect((await handlePutSlot(putRequest(started, "adder", "{}"), env, started, "adder")).status).toBe(200);

    const fresh = meetingId();
    expect((await handlePutSlot(putRequest(fresh, "adder", "{}"), limited, fresh, "adder")).status).toBe(429);
    expect((await handlePutSlot(putRequest(started, "joiner", "{}"), limited, started, "joiner")).status).toBe(200);
  });

  it("gives 503 for a new meeting while 5000 are live, but still takes slots of those", async () => {
    const started = meetingId();
    expect((await put(started, "adder", "{}")).status).toBe(200);
    await env.DB.prepare(
      `WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < ?)
       INSERT INTO meetings (id, slot, body, created) SELECT printf('ffff%028x', i), 'adder', x'00', ? FROM n`,
    )
      .bind(5000 - (await liveMeetings()), Date.now())
      .run();
    try {
      expect(await liveMeetings()).toBe(5000);
      const busy = await put(meetingId(), "adder", "{}");
      expect(busy.status).toBe(503);
      expect(await busy.json()).toEqual({ error: "The server is busy; try again later." });
      expect((await put(started, "joiner", "{}")).status).toBe(200);
    } finally {
      await env.DB.prepare("DELETE FROM meetings WHERE id LIKE 'ffff%'").run();
    }
  });
});

describe("the meeting slots", () => {
  it("give back what was put, byte for byte, with no signature", async () => {
    const id = meetingId();
    const hello = JSON.stringify({ type: "hello", v: 1, eph: "a", sign: "b", dh: "c", mac: "d" });

    expect((await put(id, "adder", hello)).status).toBe(200);

    const fetched = await get(id, "adder");
    expect(fetched.status).toBe(200);
    expect(await fetched.text()).toBe(hello);

    const bytes = crypto.getRandomValues(new Uint8Array(300));
    expect((await put(id, "welcome", bytes)).status).toBe(200);
    expect(new Uint8Array(await (await get(id, "welcome")).arrayBuffer())).toEqual(bytes);
  });

  it("include \"joined\", where the joiner leaves its proof", async () => {
    const id = meetingId();
    expect((await put(id, "joined", "{\"proof\":\"x\"}")).status).toBe(200);
    expect(await (await get(id, "joined")).text()).toBe("{\"proof\":\"x\"}");
  });

  it("include \"welcomed\", where the adder acknowledges the join, under the same rules", async () => {
    const id = meetingId();
    expect((await get(id, "welcomed")).status).toBe(404);
    expect((await put(id, "welcomed", "x".repeat(8193))).status).toBe(413);
    expect((await put(id, "welcomed", "sealed")).status).toBe(200);
    expect((await put(id, "welcomed", "again")).status).toBe(409);
    expect(await (await get(id, "welcomed")).text()).toBe("sealed");

    const start = Date.now();
    const late = meetingId();
    expect((await handlePutSlot(putRequest(late, "adder", "{}"), env, late, "adder", start)).status).toBe(200);
    const over = start + MEETING_LIFETIME_MS;
    expect((await handlePutSlot(putRequest(late, "welcomed", "sealed"), env, late, "welcomed", over)).status).toBe(410);
  });

  it("give 404 for a slot nobody has written", async () => {
    const id = meetingId();
    expect((await get(id, "joiner")).status).toBe(404);

    await put(id, "adder", "{}");
    expect((await get(id, "joiner")).status).toBe(404);
  });

  it("are each written once: a second put is 409 and the first stays", async () => {
    const id = meetingId();
    expect((await put(id, "answer", "first")).status).toBe(200);
    expect((await put(id, "answer", "second")).status).toBe(409);
    expect(await (await get(id, "answer")).text()).toBe("first");
  });

  it("hold 8 KB at most", async () => {
    const id = meetingId();
    expect((await put(id, "adder", "x".repeat(8193))).status).toBe(413);
    expect((await put(id, "adder", "x".repeat(8192))).status).toBe(200);
  });

  it("refuse an empty body", async () => {
    expect((await put(meetingId(), "adder", "")).status).toBe(400);
  });

  it("are only the four named slots of a 32-hex meeting", async () => {
    expect((await put(meetingId(), "other", "{}")).status).toBe(404);
    expect((await put("NOT-HEX", "adder", "{}")).status).toBe(404);
    expect((await put(meetingId().slice(1), "adder", "{}")).status).toBe(404);
  });

  it("end 10 minutes after the meeting's first put", async () => {
    const id = meetingId();
    const start = Date.now();
    expect((await handlePutSlot(putRequest(id, "adder", "{}"), env, id, "adder", start)).status).toBe(200);

    const nearlyOver = start + MEETING_LIFETIME_MS - 1000;
    expect((await handlePutSlot(putRequest(id, "joiner", "{}"), env, id, "joiner", nearlyOver)).status).toBe(200);
    expect((await handleGetSlot(new Request("https://example.com"), env, id, "joiner", nearlyOver)).status).toBe(200);

    const over = start + MEETING_LIFETIME_MS;
    expect((await handleGetSlot(new Request("https://example.com"), env, id, "adder", over)).status).toBe(404);
    expect((await handleGetSlot(new Request("https://example.com"), env, id, "joiner", over)).status).toBe(404);
    expect((await handlePutSlot(putRequest(id, "answer", "{}"), env, id, "answer", over)).status).toBe(410);
  });

  it("are behind the per-address rate limit", async () => {
    const limited = { ...env, ADDRESS_LIMIT: { limit: async () => ({ success: false }) } } as Cloudflare.Env;
    const id = meetingId();

    expect((await handlePutSlot(putRequest(id, "adder", "{}"), limited, id, "adder")).status).toBe(429);
    expect((await handleGetSlot(new Request("https://example.com"), limited, id, "adder")).status).toBe(429);
  });
});
