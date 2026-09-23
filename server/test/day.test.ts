import { describe, expect, it } from "vitest";
import { dayInRange, utcDateYearsAgo } from "../src/day";

const now = new Date("2026-09-24T12:00:00Z");

describe("dayInRange", () => {
  it("allows today", () => {
    expect(dayInRange("2026-09-24", now)).toBe(true);
  });

  it("allows tomorrow", () => {
    expect(dayInRange("2026-09-25", now)).toBe(true);
  });

  it("refuses the day after tomorrow", () => {
    expect(dayInRange("2026-09-26", now)).toBe(false);
  });

  it("allows exactly 15 days back", () => {
    expect(dayInRange("2026-09-09", now)).toBe(true);
  });

  it("refuses 16 days back", () => {
    expect(dayInRange("2026-09-08", now)).toBe(false);
  });
});

describe("utcDateYearsAgo", () => {
  it("subtracts whole calendar years", () => {
    expect(utcDateYearsAgo(3, now)).toBe("2023-09-24");
  });

  it("handles a leap day by landing on the nearest valid date", () => {
    expect(utcDateYearsAgo(1, new Date("2028-02-29T00:00:00Z"))).toBe("2027-03-01");
  });
});
