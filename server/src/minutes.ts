interface ColumnSpec {
  key: string;
  min: number;
  max: number;
  integer?: boolean;
  nullable?: boolean;
}

// Every minutes column but `t`, which gets its own rules (strictly rising, not just ranged).
const SPECS: readonly ColumnSpec[] = [
  { key: "avgW", min: 0, max: 5000 },
  { key: "maxW", min: 0, max: 5000 },
  { key: "cpuW", min: 0, max: 5000 },
  { key: "gpuW", min: 0, max: 5000 },
  { key: "displayW", min: 0, max: 5000 },
  { key: "ramW", min: 0, max: 5000 },
  { key: "storageW", min: 0, max: 5000 },
  { key: "boardW", min: 0, max: 5000 },
  { key: "extrasW", min: 0, max: 5000 },
  { key: "monitorsW", min: 0, max: 5000 },
  { key: "psuLossW", min: 0, max: 5000 },
  { key: "unattributedW", min: -5000, max: 5000 },
  { key: "cpuLoad", min: 0, max: 1 },
  { key: "gpuLoad", min: 0, max: 1, nullable: true },
  { key: "brightness", min: 0, max: 1, nullable: true },
  { key: "displayOnS", min: 0, max: 120 },
  { key: "idleS", min: 0, max: 120 },
  { key: "lockedS", min: 0, max: 120 },
  { key: "batteryS", min: 0, max: 120 },
  { key: "measuredS", min: 0, max: 120 },
  { key: "calibratedS", min: 0, max: 120 },
  { key: "estimatedS", min: 0, max: 120 },
  { key: "samples", min: 0, max: 100000, integer: true },
  { key: "totalSource", min: 0, max: 4, integer: true },
  { key: "gpuScope", min: 0, max: 2, integer: true },
  { key: "measuredMask", min: 0, max: 7, integer: true },
];

/**
 * Checks the minutes of a report the schema has already accepted: every column the same length,
 * at most 1500; `t` strictly rising integers 0-1499; every other value finite and in range (or,
 * for `gpuLoad`/`brightness`, null). Returns the first problem as a sentence, or null when good.
 * One pass per column, with no allocation per item, to stay inside the Worker's CPU limit.
 */
export function checkMinutes(m: Record<string, unknown>): string | null {
  const t = m.t;
  if (!Array.isArray(t)) return "t must be an array.";

  const length = t.length;
  if (length > 1500) return "t has more than 1500 minutes.";

  for (const spec of SPECS) {
    const column = m[spec.key];
    if (!Array.isArray(column)) return `${spec.key} must be an array.`;
    if (column.length !== length) return `${spec.key} is not the same length as t.`;
  }

  let previous = -1;
  for (let i = 0; i < length; i++) {
    const v = t[i];
    if (!Number.isInteger(v)) return `t[${i}] must be an integer.`;
    if ((v as number) < 0 || (v as number) > 1499) return `t[${i}] is out of range.`;
    if ((v as number) <= previous) return "t must rise strictly.";
    previous = v as number;
  }

  for (const spec of SPECS) {
    const column = m[spec.key] as unknown[];
    for (let i = 0; i < length; i++) {
      const v = column[i];
      if (spec.nullable && v === null) continue;
      const ok = spec.integer ? Number.isInteger(v) : Number.isFinite(v);
      if (!ok) return `${spec.key}[${i}] must be a number.`;
      if ((v as number) < spec.min || (v as number) > spec.max) return `${spec.key}[${i}] is out of range.`;
    }
  }

  return null;
}
