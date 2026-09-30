#!/usr/bin/env node
// The owner's Aero requests (0.10.7): lists them, and approves or revokes one by its AERO-XXXXX id. Reads the admin
// token from %USERPROFILE%\.powerledger\admin.json, as export.mjs does, and never prints it. Node 20+, no dependencies.
//
// Usage: node tools/aero.mjs --url <worker> list
//        node tools/aero.mjs --url <worker> approve AERO-XXXXX
//        node tools/aero.mjs --url <worker> revoke AERO-XXXXX

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

export const AERO_ID = /^AERO-[0-9A-HJKMNP-TV-Z]{5}$/;

/** { url, command, id } from the arguments; an id is upper-cased, as the App shows it. Throws on anything else. */
export function parseArgs(argv) {
  const args = {};
  const rest = [];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--url") args.url = argv[++i];
    else rest.push(argv[i]);
  }
  if (!args.url) throw new Error("--url is required.");
  const [command, id, ...extra] = rest;
  if (extra.length > 0) throw new Error(`Unexpected argument: ${extra[0]}`);
  if (command === "list" && id === undefined) return { ...args, command };
  if (command === "approve" || command === "revoke") {
    const upper = id?.trim().toUpperCase();
    if (!upper || !AERO_ID.test(upper)) throw new Error(`${command} needs an id, AERO- and 5 characters.`);
    return { ...args, command, id: upper };
  }
  throw new Error("Usage: aero.mjs --url <worker> list | approve <ID> | revoke <ID>");
}

/** One line per request: id, state, when requested (UTC), and the PC's name. */
export function formatList(items) {
  if (items.length === 0) return "No Aero requests.";
  return items
    .map((item) => `${item.id}  ${item.state.padEnd(8)}  ${new Date(item.requestedAt).toISOString().slice(0, 16).replace("T", " ")}  ${item.name}`)
    .join("\n");
}

function readToken() {
  const configPath = path.join(os.homedir(), ".powerledger", "admin.json");
  const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
  if (!config.token) throw new Error(`${configPath} must have a token.`);
  return config.token;
}

/** Runs one command against the Worker at `url` with `token`, through `fetchFn`; returns what to print. */
export async function run({ url, command, id }, token, fetchFn = fetch) {
  const baseUrl = url.replace(/\/+$/, "");
  const headers = { Authorization: `Bearer ${token}` };
  if (command === "list") {
    const response = await fetchFn(`${baseUrl}/v1/admin/aero`, { headers });
    if (!response.ok) throw new Error(`/v1/admin/aero gave ${response.status}.`);
    return formatList((await response.json()).items);
  }
  const response = await fetchFn(`${baseUrl}/v1/admin/aero/${command}`, {
    method: "POST",
    headers: { ...headers, "Content-Type": "application/json" },
    body: JSON.stringify({ id }),
  });
  if (response.status === 404) throw new Error(`No request with the id ${id}.`);
  if (!response.ok) throw new Error(`/v1/admin/aero/${command} gave ${response.status}.`);
  const answer = await response.json();
  return `${answer.id} is now ${answer.state}.`;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  console.log(await run(args, readToken()));
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
