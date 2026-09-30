import assert from "node:assert/strict";
import { test } from "node:test";
import { formatList, parseArgs, run } from "./aero.mjs";

const URL_ARGS = ["--url", "https://worker.example/"];

test("parseArgs takes list, and approve or revoke with an id, upper-cased", () => {
  assert.deepEqual(parseArgs([...URL_ARGS, "list"]), { url: "https://worker.example/", command: "list" });
  assert.deepEqual(parseArgs([...URL_ARGS, "approve", "aero-abc12"]), { url: "https://worker.example/", command: "approve", id: "AERO-ABC12" });
  assert.equal(parseArgs(["revoke", "AERO-ABC12", ...URL_ARGS]).command, "revoke");
});

test("parseArgs refuses a missing url, a bad id, and anything else", () => {
  assert.throws(() => parseArgs(["list"]), /--url/);
  assert.throws(() => parseArgs([...URL_ARGS, "approve"]), /id/);
  assert.throws(() => parseArgs([...URL_ARGS, "approve", "AERO-ABCDU"]), /id/);
  assert.throws(() => parseArgs([...URL_ARGS, "delete", "AERO-ABC12"]), /Usage/);
  assert.throws(() => parseArgs([...URL_ARGS, "list", "extra"]), /Usage/);
});

test("formatList gives one line a request, or says there are none", () => {
  assert.equal(formatList([]), "No Aero requests.");
  const line = formatList([{ id: "AERO-ABC12", state: "pending", requestedAt: Date.UTC(2026, 8, 30, 12, 5), name: "Study PC" }]);
  assert.equal(line, "AERO-ABC12  pending   2026-09-30 12:05  Study PC");
});

function fakeFetch(status, body) {
  const calls = [];
  const fetchFn = async (url, init = {}) => {
    calls.push({ url, init });
    return { ok: status >= 200 && status < 300, status, json: async () => body };
  };
  return { calls, fetchFn };
}

test("run approves by POST with the bearer token and the id, and never puts the token in what it prints", async () => {
  const { calls, fetchFn } = fakeFetch(200, { id: "AERO-ABC12", state: "approved" });
  const printed = await run({ url: "https://worker.example/", command: "approve", id: "AERO-ABC12" }, "tok-test", fetchFn);

  assert.equal(printed, "AERO-ABC12 is now approved.");
  assert.equal(calls[0].url, "https://worker.example/v1/admin/aero/approve");
  assert.equal(calls[0].init.method, "POST");
  assert.equal(calls[0].init.headers.Authorization, "Bearer tok-test");
  assert.deepEqual(JSON.parse(calls[0].init.body), { id: "AERO-ABC12" });
  assert.ok(!printed.includes("tok-test"));
});

test("run lists with GET, and says so for an unknown id", async () => {
  const listing = fakeFetch(200, { items: [] });
  assert.equal(await run({ url: "https://w.example", command: "list" }, "t", listing.fetchFn), "No Aero requests.");
  assert.equal(listing.calls[0].url, "https://w.example/v1/admin/aero");

  const missing = fakeFetch(404, { error: "No request with that id." });
  await assert.rejects(run({ url: "https://w.example", command: "revoke", id: "AERO-ABC12" }, "t", missing.fetchFn), /No request with the id AERO-ABC12/);
});
