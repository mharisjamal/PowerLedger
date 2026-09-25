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

export const HOUR_MS = 3_600_000;

/** One hour row as the service sends it: every samples_1h column, with plausible values. */
export function hourRow(t: number, i = 0) {
  return {
    t,
    avgW: 120 + i,
    maxW: 310,
    energyWh: 120 + i,
    cpuWh: 40,
    gpuWh: 30.5,
    displayWh: 20,
    restWh: 29.5 + i,
    idleOnWh: 10,
    idleOffWh: 0,
    idleOnS: 600,
    idleOffS: 0,
    onS: 3600,
    batteryS: 0,
    gapS: 0,
    sampleCount: 3600,
    measuredS: 0,
    calibratedS: 1800,
    estimatedS: 1800,
  };
}

/** A valid history-v1 chunk: `count` consecutive hours from `fromMs`, on consent version 2 with power on. */
export function historyBody(installId: string, fromMs: number, count: number) {
  return {
    schema: "history-v1",
    installId,
    app: "0.9.0",
    consent: { version: 2, diagnostics: true, usage: false, power: true, share: false },
    utcOffsetMinutes: 300,
    hours: Array.from({ length: count }, (_, i) => hourRow(fromMs + i * HOUR_MS, i)),
  };
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
