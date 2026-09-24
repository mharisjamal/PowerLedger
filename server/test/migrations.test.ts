import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";

// Confirms 0001_init.sql, 0002_report_bodies.sql and 0005_feedback.sql have been applied: the tables
// exist with the columns the rest of the Worker relies on.
describe("the D1 schema", () => {
  it("creates installs, reports, requests, tombstones and report_bodies", async () => {
    const names = await env.DB.prepare(
      "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name",
    ).all<{ name: string }>();

    expect(names.results.map((row) => row.name)).toEqual(
      expect.arrayContaining(["installs", "reports", "requests", "tombstones", "report_bodies", "feedback_addresses"]),
    );
  });

  it("lets a row round-trip through each table", async () => {
    await env.DB.prepare(
      `INSERT INTO installs (id, key_hash, first_seen, last_seen, consent_version, diagnostics, usage, power, share, country, app_version)
       VALUES ('11111111-1111-1111-1111-111111111111', 'hash', 1, 1, 1, 1, 0, 1, 0, 'PK', '0.6.0')`,
    ).run();
    await env.DB.prepare(
      `INSERT INTO reports (install_id, day, received_at, bytes, sections, country, r2_key)
       VALUES ('11111111-1111-1111-1111-111111111111', '2026-09-24', 1, 10, 'power', 'PK', 'reports/v1/x/2026-09-24.json.gz')`,
    ).run();
    await env.DB.prepare(
      `INSERT INTO requests (install_id, utc_day, count) VALUES ('11111111-1111-1111-1111-111111111111', '2026-09-24', 1)`,
    ).run();
    await env.DB.prepare(
      `INSERT INTO tombstones (id, deleted_at) VALUES ('22222222-2222-2222-2222-222222222222', 1)`,
    ).run();
    await env.DB.prepare(
      `INSERT INTO report_bodies (r2_key, body, content_type, received_at)
       VALUES ('reports/v1/x/2026-09-24.json.gz', ?, 'application/json', 1)`,
    )
      .bind(new Uint8Array([1, 2, 3]))
      .run();

    const install = await env.DB.prepare("SELECT * FROM installs WHERE id = ?")
      .bind("11111111-1111-1111-1111-111111111111")
      .first();
    expect(install?.country).toBe("PK");

    const byDay = await env.DB.prepare("SELECT install_id FROM reports WHERE day = ?")
      .bind("2026-09-24")
      .all();
    expect(byDay.results).toHaveLength(1);

    const body = await env.DB.prepare("SELECT content_type FROM report_bodies WHERE r2_key = ?")
      .bind("reports/v1/x/2026-09-24.json.gz")
      .first<{ content_type: string }>();
    expect(body?.content_type).toBe("application/json");
  });
});
