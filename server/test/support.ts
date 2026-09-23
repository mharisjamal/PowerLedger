/** Shared helpers for the Worker's own tests: building request bodies and test-only identities. */

export async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([bytes]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export function gzipJson(value: unknown): Promise<Uint8Array> {
  return gzip(new TextEncoder().encode(JSON.stringify(value)));
}

function base64url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** A fresh, well-formed 43-character base64url bearer key, as a real install would have. */
export function randomKey(): string {
  return base64url(crypto.getRandomValues(new Uint8Array(32)));
}

/** A fresh install id, so tests never collide with each other over shared D1 state. */
export function randomInstallId(): string {
  return crypto.randomUUID();
}
