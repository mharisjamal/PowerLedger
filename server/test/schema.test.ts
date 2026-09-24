import { describe, expect, it } from "vitest";
import { firstSchemaError } from "../src/schema";
import validFull from "./fixtures/valid-full.json";
import invalidUnknownField from "./fixtures/invalid-schema-unknown-field.json";
import invalidShareWithoutPower from "./fixtures/invalid-schema-share-without-power.json";

describe("firstSchemaError", () => {
  it("is null for a valid report", () => {
    expect(firstSchemaError(validFull)).toBeNull();
  });

  it("names the instance and the problem for an unknown field", () => {
    const error = firstSchemaError(invalidUnknownField);
    expect(error).not.toBeNull();
    expect(typeof error?.instanceLocation).toBe("string");
    expect(typeof error?.error).toBe("string");
  });

  it("accepts each architecture PowerLedger is built for, and no other", () => {
    for (const arch of ["x64", "arm64", "x86"]) {
      expect(firstSchemaError({ ...validFull, arch })).toBeNull();
    }
    expect(firstSchemaError({ ...validFull, arch: "arm32" })).not.toBeNull();
  });

  it("refuses share without power", () => {
    expect(firstSchemaError(invalidShareWithoutPower)).not.toBeNull();
  });
});
