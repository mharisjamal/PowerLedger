import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

describe("an unknown route", () => {
  it("gives 404 JSON", async () => {
    const response = await SELF.fetch("https://example.com/nowhere");

    expect(response.status).toBe(404);
    expect(await response.json()).toEqual({ error: "Not found." });
  });
});
