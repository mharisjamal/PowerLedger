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
  return new Request(`https://example.com/v1/meetings/${id}/${slot}`, { method: "PUT", body });
}

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
