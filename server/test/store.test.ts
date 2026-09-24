import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { deleteBodies, getBodies, getBody, putBody } from "../src/store";
import { withoutR2 } from "./support";

function freshKey(): string {
  return `reports/v1/store-test/${crypto.randomUUID()}.json.gz`;
}

const META = { contentType: "application/json", receivedAt: 1_700_000_000_000 };

describe("store, with R2 bound", () => {
  it("put writes only to R2; get and delete work through it", async () => {
    const key = freshKey();
    const bytes = new Uint8Array([1, 2, 3, 4]);

    await putBody(env, key, bytes, META);

    const object = await env.REPORTS!.get(key);
    expect(object).not.toBeNull();
    expect(new Uint8Array(await object!.arrayBuffer())).toEqual(bytes);
    expect(await env.DB.prepare("SELECT 1 FROM report_bodies WHERE r2_key = ?").bind(key).first()).toBeNull();

    expect(await getBody(env, key)).toEqual(bytes);

    await deleteBodies(env, [key]);
    expect(await env.REPORTS!.get(key)).toBeNull();
    expect(await getBody(env, key)).toBeNull();
  });

  it("get falls back to D1 for a body stored before R2 was enabled, and delete still removes it", async () => {
    const key = freshKey();
    const bytes = new Uint8Array([5, 6, 7]);

    // Stored while REPORTS wasn't bound yet.
    await putBody(withoutR2(env), key, bytes, META);
    expect(await env.REPORTS!.get(key)).toBeNull();

    // R2 is enabled later: R2 is checked first and misses, D1 answers instead.
    expect(await getBody(env, key)).toEqual(bytes);

    // Deleting with R2 bound clears the D1 leftover too.
    await deleteBodies(env, [key]);
    expect(await env.DB.prepare("SELECT 1 FROM report_bodies WHERE r2_key = ?").bind(key).first()).toBeNull();
  });

  it("prefers R2 over a same-keyed D1 leftover", async () => {
    const key = freshKey();
    await env.DB.prepare(
      "INSERT INTO report_bodies (r2_key, body, content_type, received_at) VALUES (?, ?, ?, ?)",
    )
      .bind(key, new Uint8Array([9, 9, 9]), META.contentType, META.receivedAt)
      .run();
    await env.REPORTS!.put(key, new Uint8Array([1, 1, 1]));

    expect(await getBody(env, key)).toEqual(new Uint8Array([1, 1, 1]));
  });

  it("deleteBodies is a no-op for an empty list", async () => {
    await expect(deleteBodies(env, [])).resolves.toBeUndefined();
  });
});

describe("store, without R2 bound", () => {
  it("put, get and delete all go through D1", async () => {
    const key = freshKey();
    const bytes = new Uint8Array([8, 9, 10]);
    const noR2 = withoutR2(env);

    await putBody(noR2, key, bytes, META);
    expect(await getBody(noR2, key)).toEqual(bytes);

    const row = await env.DB.prepare("SELECT content_type, received_at FROM report_bodies WHERE r2_key = ?")
      .bind(key)
      .first<{ content_type: string; received_at: number }>();
    expect(row).toEqual({ content_type: META.contentType, received_at: META.receivedAt });

    await deleteBodies(noR2, [key]);
    expect(await getBody(noR2, key)).toBeNull();
  });

  it("replaces the row when the same key is stored again", async () => {
    const key = freshKey();
    const noR2 = withoutR2(env);

    await putBody(noR2, key, new Uint8Array([1]), META);
    await putBody(noR2, key, new Uint8Array([2, 2]), META);

    expect(await getBody(noR2, key)).toEqual(new Uint8Array([2, 2]));
    const count = await env.DB.prepare("SELECT COUNT(*) AS n FROM report_bodies WHERE r2_key = ?")
      .bind(key)
      .first<{ n: number }>();
    expect(count?.n).toBe(1);
  });

  it("returns null for a missing key", async () => {
    expect(await getBody(withoutR2(env), freshKey())).toBeNull();
  });

  it("reads and deletes many bodies with one statement each way, not one a key", async () => {
    const noR2 = withoutR2(env);
    const keys = Array.from({ length: 30 }, () => freshKey());
    for (const [i, key] of keys.entries()) await putBody(noR2, key, new Uint8Array([i]), META);
    let statements = 0;
    const counting = {
      ...noR2,
      DB: new Proxy(noR2.DB, {
        get(object, property) {
          if (property === "prepare") {
            return (sql: string) => {
              statements++;
              return object.prepare(sql);
            };
          }
          const value = Reflect.get(object, property);
          return typeof value === "function" ? value.bind(object) : value;
        },
      }),
    };

    const bodies = await getBodies(counting, [...keys, freshKey()]);
    expect(statements).toBe(1);
    expect(bodies.size).toBe(30);
    expect(bodies.get(keys[7])).toEqual(new Uint8Array([7]));

    await deleteBodies(counting, keys);
    expect(statements).toBe(2);
    expect(await getBody(noR2, keys[7])).toBeNull();
  });
});
