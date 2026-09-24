const D1_BATCH_SIZE = 1000;

export interface BodyMeta {
  contentType: string;
  receivedAt: number;
}

/**
 * Where report bodies live: R2 when the account has it bound (env.REPORTS), or D1's
 * `report_bodies` table when it isn't (the account's R2 isn't enabled yet - see wrangler.toml).
 * Reads and deletes always check D1 too when R2 is bound, so a body stored before R2 was enabled
 * stays reachable and gets cleaned up normally, with no migration step needed once R2 is turned on.
 */

/** Stores a body: to R2 when bound, to D1 otherwise. A second put for the same key replaces the first. */
export async function putBody(env: Cloudflare.Env, key: string, bytes: Uint8Array, meta: BodyMeta): Promise<void> {
  if (env.REPORTS) {
    await env.REPORTS.put(key, bytes, { httpMetadata: { contentType: meta.contentType } });
    return;
  }

  await env.DB.prepare(
    `INSERT INTO report_bodies (r2_key, body, content_type, received_at) VALUES (?, ?, ?, ?)
     ON CONFLICT (r2_key) DO UPDATE SET
       body = excluded.body, content_type = excluded.content_type, received_at = excluded.received_at`,
  )
    .bind(key, bytes, meta.contentType, meta.receivedAt)
    .run();
}

/** Reads a body back, or null when neither store has it. R2 is checked first when bound, falling
 * back to D1 (where a body from before R2 was enabled may still be); D1 alone when unbound. */
export async function getBody(env: Cloudflare.Env, key: string): Promise<Uint8Array | null> {
  if (env.REPORTS) {
    const object = await env.REPORTS.get(key);
    if (object) return new Uint8Array(await object.arrayBuffer());
  }

  const row = await env.DB.prepare("SELECT body FROM report_bodies WHERE r2_key = ?")
    .bind(key)
    .first<{ body: ArrayBuffer }>();
  return row ? new Uint8Array(row.body) : null;
}

/** Deletes bodies from both R2 and D1 when bound (cleaning up any leftover from before R2 was
 * enabled too), or D1 alone when unbound. Safe to call with keys that don't exist. */
export async function deleteBodies(env: Cloudflare.Env, keys: string[]): Promise<void> {
  if (keys.length === 0) return;

  const reports = env.REPORTS;
  if (reports) {
    for (let i = 0; i < keys.length; i += D1_BATCH_SIZE) {
      await reports.delete(keys.slice(i, i + D1_BATCH_SIZE));
    }
  }

  for (let i = 0; i < keys.length; i += D1_BATCH_SIZE) {
    const batch = keys.slice(i, i + D1_BATCH_SIZE);
    await env.DB.batch(batch.map((key) => env.DB.prepare("DELETE FROM report_bodies WHERE r2_key = ?").bind(key)));
  }
}
