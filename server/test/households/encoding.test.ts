import { describe, expect, it, vi } from "vitest";
import { base64urlDecode, base64urlEncode, fallbackDecode, fallbackEncode } from "../../src/households/encoding";

describe("base64url", () => {
  it("round-trips every length, the native and the fallback agreeing", () => {
    for (let length = 0; length <= 70; length++) {
      const bytes = crypto.getRandomValues(new Uint8Array(length));
      const text = base64urlEncode(bytes);
      expect(text).toMatch(/^[A-Za-z0-9_-]*$/);
      expect(fallbackEncode(bytes)).toBe(text);
      expect(base64urlDecode(text)).toEqual(bytes);
      expect(fallbackDecode(text)).toEqual(bytes);
    }
  });

  it("refuses padding, whitespace, the standard alphabet and impossible lengths", () => {
    for (const text of ["AAAA=", "AA AA", "AA+/", "A", "AAAAA", "AA\nAA"]) {
      expect(base64urlDecode(text), text).toBeNull();
      expect(fallbackDecode(text), text).toBeNull();
    }
  });

  it("uses the runtime's own base64, which a batch page of megabytes needs to stay inside a Worker's CPU time", () => {
    const native = Uint8Array as unknown as { fromBase64: (...args: unknown[]) => Uint8Array; prototype: { toBase64: () => string } };
    const toBase64 = vi.spyOn(native.prototype, "toBase64");
    const fromBase64 = vi.spyOn(native, "fromBase64");

    const bytes = crypto.getRandomValues(new Uint8Array(1000));
    expect(base64urlDecode(base64urlEncode(bytes))).toEqual(bytes);

    expect(toBase64).toHaveBeenCalledOnce();
    expect(fromBase64).toHaveBeenCalledOnce();
    toBase64.mockRestore();
    fromBase64.mockRestore();
  });
});
