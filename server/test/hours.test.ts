/// <reference types="vite/client" />
import { Validator, type Schema } from "@cfworker/json-schema";
import { describe, expect, it } from "vitest";
import historySchema from "../schema/history-v1.schema.json";
import { checkHours, HOUR_COLUMNS } from "../src/hours";
import { historyBody, HOUR_MS, hourRow } from "./support";

// The client's own table, read as text: the history's columns must mirror samples_1h exactly.
const clientSchema = Object.values(
  import.meta.glob("../../src/PowerLedger.Storage/Schema.cs", { query: "?raw", import: "default", eager: true }),
)[0] as string;

const NOW = Date.UTC(2026, 8, 25, 12);

describe("the hour columns", () => {
  it("are samples_1h's columns, in camelCase, start_ms as t", () => {
    const table = /CREATE TABLE samples_1h \(([^;]*?)\);/.exec(clientSchema)?.[1];
    expect(table).toBeDefined();
    const columns = [...table!.matchAll(/([a-z_0-9]+)\s+(?:INTEGER|REAL)/g)].map((match) => match[1]);
    const camel = columns.map((name) => (name === "start_ms" ? "t" : name.replace(/_([a-z0-9])/g, (_, c: string) => c.toUpperCase())));

    expect(HOUR_COLUMNS).toEqual(camel);
  });

  it("are what the full schema requires of an hour, so the schema and the check agree", () => {
    const validator = new Validator(historySchema as Schema, "2020-12", false);
    expect(validator.validate(historyBody("0f8fad5b-d9cb-469f-a165-70867728950e", NOW - 10 * HOUR_MS, 3)).valid).toBe(true);
    expect(historySchema.$defs.hour.required).toEqual(HOUR_COLUMNS);
  });
});

describe("checkHours", () => {
  const hours = (count: number) => Array.from({ length: count }, (_, i) => hourRow(NOW - 800 * HOUR_MS + i * HOUR_MS, i));

  it("is null for good hours, up to 744 of them", () => {
    expect(checkHours(hours(1), NOW)).toBeNull();
    expect(checkHours(hours(744), NOW)).toBeNull();
  });

  it("takes a rest below zero and a long gap, as the PC records them", () => {
    const rows = hours(1);
    rows[0].restWh = -3.2;
    rows[0].gapS = 86_400;
    expect(checkHours(rows, NOW)).toBeNull();
  });

  it.each([
    ["a row that isn't an object", (rows: unknown[]) => { rows[0] = 5; }, "hours[0] must be an object."],
    ["a missing column", (rows: unknown[]) => { delete (rows[0] as Record<string, unknown>).onS; }, "hours[0] must have exactly the 19 hour columns."],
    ["an extra column", (rows: unknown[]) => { (rows[0] as Record<string, unknown>).x = 1; }, "hours[0] must have exactly the 19 hour columns."],
    ["a column swapped for another", (rows: unknown[]) => {
      const row = rows[0] as Record<string, unknown>;
      delete row.onS;
      row.offS = 1;
    }, "hours[0].onS is missing."],
    ["t off the hour", (rows: unknown[]) => { (rows[1] as { t: number }).t += 1; }, "hours[1].t must be on the hour."],
    ["t not rising", (rows: unknown[]) => { (rows[1] as { t: number }).t -= HOUR_MS; }, "hours must rise strictly by t."],
    ["t in the future", (rows: unknown[]) => { (rows[1] as { t: number }).t = NOW + HOUR_MS; }, "hours[1].t is out of range."],
    ["t before 2020", (rows: unknown[]) => { (rows[0] as { t: number }).t = Date.UTC(2019, 0, 1); }, "hours[0].t is out of range."],
    ["a value out of range", (rows: unknown[]) => { (rows[1] as { onS: number }).onS = 7201; }, "hours[1].onS is out of range."],
    ["null", (rows: unknown[]) => { (rows[1] as Record<string, unknown>).cpuWh = null; }, "hours[1].cpuWh must be a number."],
    ["a fraction of a sample", (rows: unknown[]) => { (rows[1] as { sampleCount: number }).sampleCount = 0.5; }, "hours[1].sampleCount must be a number."],
  ])("names %s", (_name, spoil, message) => {
    const rows: unknown[] = hours(3);
    spoil(rows);
    expect(checkHours(rows, NOW)).toBe(message);
  });

  it("refuses more than 31 days from the first hour to the end of the last", () => {
    const rows = hours(2);
    rows[1].t = rows[0].t + 744 * HOUR_MS;
    expect(checkHours(rows, NOW)).toBe("hours span more than 31 days.");
    rows[1].t = rows[0].t + 743 * HOUR_MS;
    expect(checkHours(rows, NOW)).toBeNull();
  });
});
