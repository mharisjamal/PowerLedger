import { describe, expect, it } from "vitest";
import { readBounded } from "../src/body";

/** A body that hands out `chunks` chunks of `size` bytes, counting how many were asked for. */
function counted(chunks: number, size: number): { body: ReadableStream<Uint8Array>; pulls: () => number } {
  let pulled = 0;
  const body = new ReadableStream<Uint8Array>(
    {
      pull(controller) {
        if (pulled >= chunks) {
          controller.close();
          return;
        }
        pulled++;
        controller.enqueue(new Uint8Array(size));
      },
    },
    { highWaterMark: 0 },
  );
  return { body, pulls: () => pulled };
}

function post(body: BodyInit | null, headers: Record<string, string> = {}): Request {
  return new Request("https://example.com/", { method: "POST", body, headers });
}

describe("readBounded", () => {
  it("returns a body within the limit whole", async () => {
    const bytes = await readBounded(post(new Uint8Array([1, 2, 3])), 3);
    expect(Array.from(bytes ?? [])).toEqual([1, 2, 3]);
  });

  it("returns nothing for no body", async () => {
    expect((await readBounded(post(null), 3))?.byteLength).toBe(0);
  });

  it("refuses a body over the limit", async () => {
    expect(await readBounded(post(new Uint8Array(10)), 4)).toBeNull();
  });

  it("refuses a declared length over the limit", async () => {
    expect(await readBounded(post("x", { "Content-Length": "999999999" }), 4)).toBeNull();
  });

  it("stops reading a streamed body once it is past the limit, without taking the rest", async () => {
    const { body, pulls } = counted(100, 4);
    expect(await readBounded(post(body), 6)).toBeNull();
    expect(pulls()).toBeLessThanOrEqual(3);
  });
});
