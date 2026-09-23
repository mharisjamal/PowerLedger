import { describe, expect, it } from "vitest";
import { checkMinutes } from "../src/minutes";
import validFull from "./fixtures/valid-full.json";
import validLaptop from "./fixtures/valid-laptop-power-only.json";
import invalidNotRising from "./fixtures/invalid-minutes-not-rising.json";
import invalidNullLoad from "./fixtures/invalid-minutes-null-load.json";
import invalidOutOfRange from "./fixtures/invalid-minutes-out-of-range.json";
import invalidUnequalLengths from "./fixtures/invalid-minutes-unequal-lengths.json";

// A minimal, in-range minutes object of the given length, for the edge cases the fixtures don't cover.
// Every column gets its own array (never shared) so a test can mutate one column in isolation.
function baseMinutes(length: number): Record<string, unknown> {
  const weightColumns = [
    "avgW", "maxW", "cpuW", "gpuW", "displayW", "ramW", "storageW", "boardW", "extrasW", "monitorsW", "psuLossW",
  ];
  const secondColumns = ["displayOnS", "idleS", "lockedS", "batteryS", "measuredS", "calibratedS", "estimatedS"];

  const m: Record<string, unknown> = { t: Array.from({ length }, (_, i) => i) };
  for (const key of weightColumns) m[key] = Array<number>(length).fill(10);
  m.unattributedW = Array<number>(length).fill(0);
  m.cpuLoad = Array<number>(length).fill(0.5);
  m.gpuLoad = Array<number>(length).fill(0.5);
  m.brightness = Array<number>(length).fill(0.5);
  for (const key of secondColumns) m[key] = Array<number>(length).fill(30);
  m.samples = Array<number>(length).fill(60);
  m.totalSource = Array<number>(length).fill(0);
  m.gpuScope = Array<number>(length).fill(0);
  m.measuredMask = Array<number>(length).fill(0);
  return m;
}

describe("checkMinutes", () => {
  it.each([
    ["valid-full.json", validFull],
    ["valid-laptop-power-only.json", validLaptop],
  ])("passes %s", (_name, fixture) => {
    expect(checkMinutes((fixture as { power: { minutes: Record<string, unknown> } }).power.minutes)).toBeNull();
  });

  it.each([
    ["invalid-minutes-not-rising.json", invalidNotRising],
    ["invalid-minutes-null-load.json", invalidNullLoad],
    ["invalid-minutes-out-of-range.json", invalidOutOfRange],
    ["invalid-minutes-unequal-lengths.json", invalidUnequalLengths],
  ])("fails %s", (_name, fixture) => {
    expect(checkMinutes((fixture as { power: { minutes: Record<string, unknown> } }).power.minutes)).not.toBeNull();
  });

  it("passes an empty day", () => {
    expect(checkMinutes(baseMinutes(0))).toBeNull();
  });

  it("fails 1501 items", () => {
    expect(checkMinutes(baseMinutes(1501))).not.toBeNull();
  });

  it("fails a float in samples", () => {
    const m = baseMinutes(3);
    (m.samples as number[])[1] = 60.5;
    expect(checkMinutes(m)).not.toBeNull();
  });

  it("fails NaN given as a string", () => {
    const m = baseMinutes(3);
    (m.avgW as unknown[])[1] = "NaN";
    expect(checkMinutes(m)).not.toBeNull();
  });

  it("fails cpuLoad given as null", () => {
    const m = baseMinutes(3);
    (m.cpuLoad as unknown[])[0] = null;
    expect(checkMinutes(m)).not.toBeNull();
  });

  it("allows gpuLoad and brightness to be null", () => {
    const m = baseMinutes(3);
    (m.gpuLoad as unknown[])[0] = null;
    (m.brightness as unknown[])[2] = null;
    expect(checkMinutes(m)).toBeNull();
  });

  it("fails t that repeats instead of rising", () => {
    const m = baseMinutes(3);
    (m.t as number[])[1] = (m.t as number[])[0];
    expect(checkMinutes(m)).not.toBeNull();
  });

  it("allows unattributedW down to -5000", () => {
    const m = baseMinutes(1);
    (m.unattributedW as number[])[0] = -5000;
    expect(checkMinutes(m)).toBeNull();
  });

  it("fails unattributedW below -5000", () => {
    const m = baseMinutes(1);
    (m.unattributedW as number[])[0] = -5000.1;
    expect(checkMinutes(m)).not.toBeNull();
  });
});
