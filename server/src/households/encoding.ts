/** Byte encodings the households routes share with the .NET side: base64url without padding, and lower-case hex. */

const BASE64URL = /^[A-Za-z0-9_-]*$/;
const CHUNK = 0x8000;

/** The runtime's own base64 (Uint8Array.fromBase64 and toBase64), when it has them: a batch page is megabytes, and a
 * byte-at-a-time loop over that costs far more CPU time than a Worker gets. */
interface NativeBase64 {
  fromBase64?: (text: string, options: { alphabet: "base64url" }) => Uint8Array;
}
interface NativeBase64Bytes {
  toBase64?: (options: { alphabet: "base64url"; omitPadding: boolean }) => string;
}

export function base64urlEncode(bytes: Uint8Array): string {
  const native = (bytes as Uint8Array & NativeBase64Bytes).toBase64;
  return native ? native.call(bytes, { alphabet: "base64url", omitPadding: true }) : fallbackEncode(bytes);
}

/** The bytes, or null when `text` isn't unpadded base64url. */
export function base64urlDecode(text: string): Uint8Array | null {
  if (!BASE64URL.test(text) || text.length % 4 === 1) return null;
  const native = (Uint8Array as unknown as NativeBase64).fromBase64;
  if (!native) return fallbackDecode(text);
  try {
    return native(text, { alphabet: "base64url" });
  } catch {
    return null;
  }
}

/** base64urlEncode without the runtime's help, a chunk at a time. */
export function fallbackEncode(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += CHUNK) binary += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** base64urlDecode without the runtime's help. */
export function fallbackDecode(text: string): Uint8Array | null {
  if (!BASE64URL.test(text) || text.length % 4 === 1) return null;
  const binary = atob(text.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (text.length % 4)) % 4));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

export function hex(bytes: Uint8Array): string {
  return [...bytes].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

export async function sha256(bytes: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
}
