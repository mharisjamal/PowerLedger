#!/usr/bin/env node
// Downloads and unpacks a range of reports and history for the owner: pages /admin/list, fetches
// each /admin/object, and writes NDJSON, a minutes CSV and an hours CSV. Node 20+, no dependencies.
//
// Usage: node tools/export.mjs --url <worker> --from <day> --to <day> [--shared] --out <folder>

import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import zlib from "node:zlib";

// The minutes columns, in the order report-v1.schema.json requires them.
const MINUTE_COLUMNS = [
  "t", "avgW", "maxW", "cpuW", "gpuW", "displayW", "ramW", "storageW", "boardW", "extrasW",
  "monitorsW", "psuLossW", "unattributedW", "cpuLoad", "gpuLoad", "brightness", "displayOnS",
  "idleS", "lockedS", "batteryS", "measuredS", "calibratedS", "estimatedS", "samples",
  "totalSource", "gpuScope", "measuredMask",
];

export const MINUTES_CSV_HEADER = ["pc", "day", "country", "chassis", "arch", ...MINUTE_COLUMNS];

// The hour columns, in the order history-v1.schema.json lists them: the PC's samples_1h, t its start_ms.
export const HOUR_COLUMNS = [
  "t", "avgW", "maxW", "energyWh", "cpuWh", "gpuWh", "displayWh", "restWh", "idleOnWh", "idleOffWh",
  "idleOnS", "idleOffS", "onS", "batteryS", "gapS", "sampleCount", "measuredS", "calibratedS", "estimatedS",
];

export const HOURS_CSV_HEADER = ["pc", "country", ...HOUR_COLUMNS];

const DAY_MS = 86_400_000;

/** The first 16 hex characters of HMAC-SHA256(salt, installId): stable across exports (the same
 * install always gets the same pseudonym under one salt), but never reversible to the real id. */
export function pseudonym(saltBase64Url, installId) {
  const key = Buffer.from(saltBase64Url, "base64url");
  return crypto.createHmac("sha256", key).update(installId, "utf8").digest("hex").slice(0, 16);
}

/** One CSV line: fields joined with commas, quoting only a field that needs it. */
export function csvLine(fields) {
  return fields.map(csvField).join(",");
}

/** One cell. A number is written as it is; text that a spreadsheet would read as a formula (starting with =, +, -, @,
 * a tab or a carriage return) gets an apostrophe in front, so opening the file can't run anything. */
function csvField(value) {
  if (typeof value === "number") return String(value);
  let text = value === null || value === undefined ? "" : String(value);
  if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
  return /[",\n\r]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** One line of reports.ndjson: the report with `pc` in place of its installId, and the list item's country, receipt
 * time and `complete` (false for today so far; a report from before 0.9.0 has no field and is a complete day). */
export function reportLine(report, item, pc) {
  const { installId, ...withoutInstallId } = report;
  return {
    pc,
    ...withoutInstallId,
    complete: item.complete !== false,
    country: item.country,
    receivedAt: item.receivedAt,
  };
}

/** `report`'s columnar minutes, one row per minute, ready for csvLine(); [] when there's no
 * power section. */
export function minuteRows(report, pc, country) {
  const minutes = report.power?.minutes;
  if (!minutes) return [];

  const rows = [];
  for (let i = 0; i < minutes.t.length; i++) {
    const row = [pc, report.day, country, report.chassis, report.arch];
    for (const column of MINUTE_COLUMNS) row.push(minutes[column][i]);
    rows.push(row);
  }
  return rows;
}

/** A history chunk's hours with fromMs <= t < toMs, one row each, ready for csvLine(): `pc,country,t,<columns>`. The
 * range keeps an export to the days asked for, though a chunk overlapping them holds hours either side. */
export function hourRows(history, pc, country, fromMs, toMs) {
  const rows = [];
  for (const hour of history.hours ?? []) {
    if (hour.t < fromMs || hour.t >= toMs) continue;
    rows.push([pc, country, ...HOUR_COLUMNS.map((column) => hour[column])]);
  }
  return rows;
}

function parseArgs(argv) {
  const args = { shared: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--url") args.url = argv[++i];
    else if (arg === "--from") args.from = argv[++i];
    else if (arg === "--to") args.to = argv[++i];
    else if (arg === "--out") args.out = argv[++i];
    else if (arg === "--shared") args.shared = true;
    else throw new Error(`Unknown argument: ${arg}`);
  }
  for (const name of ["url", "from", "to", "out"]) {
    if (!args[name]) throw new Error(`--${name} is required.`);
  }
  return args;
}

function readAdminConfig() {
  const configPath = path.join(os.homedir(), ".powerledger", "admin.json");
  const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
  if (!config.token || !config.salt) throw new Error(`${configPath} must have a token and a salt.`);
  return config;
}

async function* listItems(baseUrl, token, { from, to, shared }, kind = "report") {
  let after;
  for (;;) {
    const params = new URLSearchParams({ kind, from, to, limit: "1000" });
    if (shared) params.set("shared", "1");
    if (after) params.set("after", after);

    const response = await fetch(`${baseUrl}/admin/list?${params}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (!response.ok) throw new Error(`/admin/list gave ${response.status}.`);

    const page = await response.json();
    for (const item of page.items) yield item;

    if (!page.next) return;
    after = page.next;
  }
}

async function fetchReport(baseUrl, token, key) {
  const response = await fetch(`${baseUrl}/admin/object?key=${encodeURIComponent(key)}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`/admin/object gave ${response.status} for ${key}.`);

  const compressed = Buffer.from(await response.arrayBuffer());
  return JSON.parse(zlib.gunzipSync(compressed).toString("utf8"));
}

function closeStream(stream) {
  return new Promise((resolve, reject) => stream.end((error) => (error ? reject(error) : resolve())));
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const config = readAdminConfig();
  const baseUrl = args.url.replace(/\/+$/, "");

  fs.mkdirSync(args.out, { recursive: true });
  const reportsStream = fs.createWriteStream(path.join(args.out, "reports.ndjson"));
  const minutesStream = fs.createWriteStream(path.join(args.out, "minutes.csv"));
  const hardwareStream = fs.createWriteStream(path.join(args.out, "hardware.ndjson"));
  const hoursStream = fs.createWriteStream(path.join(args.out, "hours.csv"));
  minutesStream.write(csvLine(MINUTES_CSV_HEADER) + "\n");
  hoursStream.write(csvLine(HOURS_CSV_HEADER) + "\n");

  let count = 0;
  for await (const item of listItems(baseUrl, config.token, args)) {
    const report = await fetchReport(baseUrl, config.token, item.key);
    const pc = pseudonym(config.salt, item.installId);

    reportsStream.write(JSON.stringify(reportLine(report, item, pc)) + "\n");

    for (const row of minuteRows(report, pc, item.country)) minutesStream.write(csvLine(row) + "\n");

    if (report.power?.hardware) {
      hardwareStream.write(JSON.stringify({ pc, day: report.day, hardware: report.power.hardware }) + "\n");
    }

    count++;
  }

  // History: every chunk with an hour in the days asked for, clipped to them.
  const fromMs = Date.parse(`${args.from}T00:00:00Z`);
  const toMs = Date.parse(`${args.to}T00:00:00Z`) + DAY_MS;
  let chunks = 0;
  for await (const item of listItems(baseUrl, config.token, args, "history")) {
    const history = await fetchReport(baseUrl, config.token, item.key);
    const pc = pseudonym(config.salt, item.installId);
    for (const row of hourRows(history, pc, item.country, fromMs, toMs)) hoursStream.write(csvLine(row) + "\n");
    chunks++;
  }

  await Promise.all([
    closeStream(reportsStream),
    closeStream(minutesStream),
    closeStream(hardwareStream),
    closeStream(hoursStream),
  ]);
  console.log(`Wrote ${count} report(s) and ${chunks} history chunk(s) to ${args.out}.`);
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
