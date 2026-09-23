#!/usr/bin/env node
// Downloads and unpacks a range of reports for the owner: pages /admin/list, fetches each
// /admin/object, and writes NDJSON and a minutes CSV. Node 20+, no dependencies.
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

function csvField(value) {
  const text = value === null || value === undefined ? "" : String(value);
  return /[",\n\r]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
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

async function* listItems(baseUrl, token, { from, to, shared }) {
  let after;
  for (;;) {
    const params = new URLSearchParams({ from, to, limit: "1000" });
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
  minutesStream.write(csvLine(MINUTES_CSV_HEADER) + "\n");

  let count = 0;
  for await (const item of listItems(baseUrl, config.token, args)) {
    const report = await fetchReport(baseUrl, config.token, item.key);
    const pc = pseudonym(config.salt, item.installId);

    const { installId, ...withoutInstallId } = report;
    reportsStream.write(
      JSON.stringify({ pc, ...withoutInstallId, country: item.country, receivedAt: item.receivedAt }) + "\n",
    );

    for (const row of minuteRows(report, pc, item.country)) minutesStream.write(csvLine(row) + "\n");

    if (report.power?.hardware) {
      hardwareStream.write(JSON.stringify({ pc, day: report.day, hardware: report.power.hardware }) + "\n");
    }

    count++;
  }

  await Promise.all([closeStream(reportsStream), closeStream(minutesStream), closeStream(hardwareStream)]);
  console.log(`Wrote ${count} report(s) to ${args.out}.`);
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
