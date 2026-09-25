import assert from "node:assert/strict";
import crypto from "node:crypto";
import { test } from "node:test";
import fs from "node:fs";
import {
  csvLine,
  HOUR_COLUMNS,
  hourRows,
  HOURS_CSV_HEADER,
  minuteRows,
  MINUTES_CSV_HEADER,
  pseudonym,
  reportLine,
} from "./export.mjs";

test("the hour columns are history-v1.schema.json's, in its order", () => {
  const schema = JSON.parse(fs.readFileSync(new URL("../schema/history-v1.schema.json", import.meta.url), "utf8"));
  assert.deepEqual(HOUR_COLUMNS, Object.keys(schema.$defs.hour.properties));
  assert.deepEqual(HOURS_CSV_HEADER, ["pc", "country", ...HOUR_COLUMNS]);
});

test("hourRows gives pc, country and every column an hour, only for hours in the range", () => {
  const hour = (t) => Object.fromEntries(HOUR_COLUMNS.map((column, i) => [column, column === "t" ? t : i]));
  const history = { schema: "history-v1", installId: "x", hours: [hour(0), hour(3_600_000), hour(7_200_000)] };

  const rows = hourRows(history, "abc123", "PK", 3_600_000, 7_200_000);

  assert.equal(rows.length, 1);
  assert.deepEqual(rows[0].slice(0, 3), ["abc123", "PK", 3_600_000]);
  assert.equal(rows[0].length, HOURS_CSV_HEADER.length);
  assert.equal(csvLine(rows[0]).includes("x"), false);
});

test("reportLine swaps installId for pc and carries complete, country and receipt time", () => {
  const report = { schema: 1, installId: "11111111-1111-1111-1111-111111111111", day: "2026-09-24", complete: false };

  const partial = reportLine(report, { country: "PK", receivedAt: 5, complete: false }, "abc123");
  assert.deepEqual(partial, { pc: "abc123", schema: 1, day: "2026-09-24", complete: false, country: "PK", receivedAt: 5 });

  const { complete, ...older } = report;
  assert.equal(reportLine(older, { country: "PK", receivedAt: 5, complete: true }, "abc123").complete, true);
});

function randomSalt() {
  return Buffer.from(crypto.randomBytes(32)).toString("base64url");
}

test("pseudonym is deterministic and 16 hex characters", () => {
  const salt = randomSalt();
  const a = pseudonym(salt, "11111111-1111-1111-1111-111111111111");
  const b = pseudonym(salt, "11111111-1111-1111-1111-111111111111");

  assert.equal(a, b);
  assert.match(a, /^[0-9a-f]{16}$/);
});

test("pseudonym differs for a different install, or a different salt", () => {
  const salt = randomSalt();
  const a = pseudonym(salt, "11111111-1111-1111-1111-111111111111");
  const b = pseudonym(salt, "22222222-2222-2222-2222-222222222222");
  assert.notEqual(a, b);

  const c = pseudonym(randomSalt(), "11111111-1111-1111-1111-111111111111");
  assert.notEqual(a, c);
});

test("csvLine joins plain fields with commas", () => {
  assert.equal(csvLine(["a", "b", "c"]), "a,b,c");
  assert.equal(csvLine(["PK", 600, 142.3, null, undefined, false]), "PK,600,142.3,,,false");
});

test("csvLine quotes a field with a comma, a quote or a newline", () => {
  assert.equal(csvLine(["has,comma", "plain"]), '"has,comma",plain');
  assert.equal(csvLine(['has"quote']), '"has""quote"');
  assert.equal(csvLine(["has\nnewline"]), '"has\nnewline"');
});

function fullMinutes(length) {
  const minutes = {};
  for (const column of MINUTES_CSV_HEADER.slice(5)) {
    minutes[column] = Array.from({ length }, (_, i) => (column === "t" ? i : i + 1));
  }
  return minutes;
}

test("minuteRows explodes a report's columnar minutes into one row per minute", () => {
  const report = {
    day: "2026-09-24",
    chassis: "desktop",
    arch: "x64",
    power: { hardware: null, minutes: fullMinutes(2) },
  };

  const rows = minuteRows(report, "abc123", "PK");

  assert.equal(rows.length, 2);
  assert.deepEqual(rows[0].slice(0, 6), ["abc123", "2026-09-24", "PK", "desktop", "x64", 0]);
  assert.deepEqual(rows[1].slice(0, 6), ["abc123", "2026-09-24", "PK", "desktop", "x64", 1]);
  assert.equal(rows[0].length, MINUTES_CSV_HEADER.length);
});

test("minuteRows is empty when the report has no power section", () => {
  const rows = minuteRows({ day: "2026-09-24", chassis: "desktop", arch: "x64" }, "abc123", "PK");
  assert.deepEqual(rows, []);
});

test("csvLine keeps a text cell from being read as a formula, and leaves numbers alone", () => {
  assert.equal(
    csvLine(["=SUM(A1)", "+1", "@cmd", "-cmd", "	x", -1.3, 5, "plain"]),
    "'=SUM(A1),'+1,'@cmd,'-cmd,'	x,-1.3,5,plain",
  );
});
