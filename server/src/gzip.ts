/** Compresses `bytes` as gzip. */
export async function gzipBytes(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/**
 * Decompresses `bytes` as gzip, streaming so a zip bomb is caught before it grows unbounded: once
 * the decompressed output passes `max` bytes, the stream is cancelled and `null` is returned.
 * Bytes that aren't valid gzip throw; the caller turns that into a 400.
 */
export async function gunzipBounded(bytes: Uint8Array, max: number): Promise<Uint8Array | null> {
  const source = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
  const reader = source.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    total += value.byteLength;
    if (total > max) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }

  const result = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return result;
}
