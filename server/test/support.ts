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

/** A fresh IPv4 address for `CF-Connecting-IP`, so a test sending many requests never uses up ADDRESS_LIMIT (60 a
 * minute) for the tests that share the default address. */
export function randomAddress(): string {
  const [a, b, c] = crypto.getRandomValues(new Uint8Array(3));
  return `10.${a}.${b}.${c}`;
}

/** A copy of `env` as it would be before R2 is enabled on the account: no REPORTS binding. */
export function withoutR2(env: Cloudflare.Env): Cloudflare.Env {
  return { ...env, REPORTS: undefined };
}
