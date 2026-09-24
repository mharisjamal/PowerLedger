import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";

async function columns(table: string): Promise<string[]> {
  const rows = await env.DB.prepare(`SELECT name FROM pragma_table_info('${table}') ORDER BY cid`).all<{ name: string }>();
  return rows.results.map((row) => row.name);
}

// 0003_households.sql: the tables the household routes rely on (households design §8).
describe("the households schema", () => {
  it("creates the household tables with their columns", async () => {
    expect(await columns("households")).toEqual(["id", "created", "epoch"]);
    expect(await columns("members")).toEqual(["household", "device", "sign_key", "dh_key", "added", "removed"]);
    expect(await columns("batches")).toEqual([
      "household", "seq", "device", "epoch", "device_seq", "bytes", "received", "r2_key",
    ]);
    expect(await columns("key_envelopes")).toEqual(["household", "epoch", "device", "from_device", "body", "created"]);
    expect(await columns("meetings")).toEqual(["id", "slot", "body", "created"]);
    expect(await columns("device_requests")).toEqual(["device", "utc_day", "count", "batches", "batch_bytes"]);
    expect(await columns("daily_totals")).toEqual(["utc_day", "batch_bytes"]);
    expect(await columns("seen_signatures")).toEqual(["device", "r", "seen"]);
  });

  it("creates the sign-in tables (0004) with their columns", async () => {
    expect(await columns("accounts")).toEqual(["id", "provider", "subject", "created"]);
    expect(await columns("sessions")).toEqual(["token_hash", "account", "device", "sign_key", "dh_key", "created"]);
    expect(await columns("account_households")).toEqual(["account", "household", "linked"]);
    expect(await columns("join_requests")).toEqual(["household", "device", "account", "sign_key", "dh_key", "created"]);
    expect(await columns("recovery")).toEqual(["account", "body", "verifier_hash", "epoch", "updated"]);
  });

  it("keys members by household and device, and batches by household and seq", async () => {
    const hid = "a".repeat(32);
    const insertMember = () =>
      env.DB.prepare("INSERT INTO members (household, device, sign_key, dh_key, added) VALUES (?, ?, 's', 'd', 1)")
        .bind(hid, "b".repeat(32))
        .run();
    await insertMember();
    await expect(insertMember()).rejects.toThrow();

    const member = await env.DB.prepare("SELECT removed FROM members WHERE household = ?").bind(hid).first();
    expect(member).toEqual({ removed: null });

    const insertBatch = () =>
      env.DB.prepare(
        `INSERT INTO batches (household, seq, device, epoch, device_seq, bytes, received, r2_key)
         VALUES (?, 1, ?, 1, 1, 28, 1, 'batches/v1/x')`,
      )
        .bind(hid, "b".repeat(32))
        .run();
    await insertBatch();
    await expect(insertBatch()).rejects.toThrow();
  });
});
