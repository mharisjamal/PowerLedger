import { describe, expect, it } from "vitest";
import { gunzipBounded } from "../src/gzip";

async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

describe("gunzipBounded", () => {
  it("round-trips a small body", async () => {
    const body = new TextEncoder().encode('{"hello":"world"}');
    const zipped = await gzip(body);

    const result = await gunzipBounded(zipped, 1024);

    expect(result).toEqual(body);
  });

  it("stops a zip bomb before it grows past the limit", async () => {
    const bomb = await gzip(new Uint8Array(10 * 1024 * 1024)); // 10 MiB of zeros, tiny once gzipped

    const result = await gunzipBounded(bomb, 8 * 1024 * 1024);

    expect(result).toBeNull();
  });

  it("throws on bytes that aren't gzip", async () => {
    const notGzip = new TextEncoder().encode("this is not gzip data at all");

    await expect(gunzipBounded(notGzip, 1024)).rejects.toBeTruthy();
  });
});
