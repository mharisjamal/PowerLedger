import historySchema from "../schema/history-v1.schema.json";

const HOUR_MS = 3_600_000;
/** A chunk is at most 31 days: its first hour's start to its last hour's end. */
const MAX_SPAN_MS = 31 * 24 * HOUR_MS;

interface ColumnSpec {
  key: string;
  min: number;
  max: number;
  integer: boolean;
}

interface PropertySchema {
  type: string;
  minimum: number;
  maximum?: number;
}

// Built from the schema's own `hour` definition, so the columns and their ranges are written down once. Every column
// but `t`, which gets its own rules (on the hour, strictly rising, not in the future).
const properties = historySchema.$defs.hour.properties as Record<string, PropertySchema>;
const SPECS: readonly ColumnSpec[] = Object.entries(properties)
  .filter(([key]) => key !== "t")
  .map(([key, spec]) => ({ key, min: spec.minimum, max: spec.maximum ?? Infinity, integer: spec.type === "integer" }));
const COLUMN_COUNT = SPECS.length + 1;
const MIN_T = properties.t.minimum;

/** Every hour column's name, `t` first, in the schema's order. */
export const HOUR_COLUMNS: readonly string[] = Object.keys(properties);

/**
 * Checks the hours of a history chunk the schema has already accepted as an array of objects: each row exactly the
 * schema's columns; `t` a whole hour in UTC ms, strictly rising, none in the future (after `nowMs`), the chunk
 * spanning at most 31 days; every other value a finite number in range (whole where the schema says integer).
 * Returns the first problem as a sentence, or null when good. One pass, no allocation per row, to stay inside the
 * Worker's CPU limit.
 */
export function checkHours(hours: unknown[], nowMs: number): string | null {
  let previous = -Infinity;
  for (let i = 0; i < hours.length; i++) {
    const row = hours[i] as Record<string, unknown>;
    if (typeof row !== "object" || row === null || Array.isArray(row)) return `hours[${i}] must be an object.`;

    // Exactly the schema's columns: as many keys as there are columns, and every column present.
    let keys = 0;
    for (const _ in row) keys++;
    if (keys !== COLUMN_COUNT) return `hours[${i}] must have exactly the ${COLUMN_COUNT} hour columns.`;

    const t = row.t;
    if (!Number.isInteger(t)) return `hours[${i}].t must be an integer.`;
    if ((t as number) % HOUR_MS !== 0) return `hours[${i}].t must be on the hour.`;
    if ((t as number) < MIN_T || (t as number) > nowMs) return `hours[${i}].t is out of range.`;
    if ((t as number) <= previous) return "hours must rise strictly by t.";
    previous = t as number;

    for (const spec of SPECS) {
      const v = row[spec.key];
      if (v === undefined) return `hours[${i}].${spec.key} is missing.`;
      const ok = spec.integer ? Number.isInteger(v) : Number.isFinite(v);
      if (!ok) return `hours[${i}].${spec.key} must be a number.`;
      if ((v as number) < spec.min || (v as number) > spec.max) return `hours[${i}].${spec.key} is out of range.`;
    }
  }

  const first = (hours[0] as { t: number }).t;
  if (previous + HOUR_MS - first > MAX_SPAN_MS) return "hours span more than 31 days.";
  return null;
}
