import { utcDateString, utcDateYearsAgo } from "./day";
import { deleteBodies } from "./store";

const BATCH_SIZE = 1000;
const MAX_BATCHES = 10;
const RETENTION_YEARS = 3;
const REQUEST_RETENTION_DAYS = 2;

interface ReportKeyRow {
  install_id: string;
  day: string;
  r2_key: string;
}

/**
 * The daily cron: deletes reports (and their R2 objects) whose day is more than 3 years old, and
 * request counts more than 2 days old. Reports are worked off 1000 at a time, at most 10 batches
 * a run, so a large backlog is bounded and simply continues on the next run.
 */
export async function runRetention(env: Cloudflare.Env, now: Date = new Date()): Promise<void> {
  const cutoff = utcDateYearsAgo(RETENTION_YEARS, now);

  for (let batch = 0; batch < MAX_BATCHES; batch++) {
    const rows = await env.DB.prepare("SELECT install_id, day, r2_key FROM reports WHERE day < ? LIMIT ?")
      .bind(cutoff, BATCH_SIZE)
      .all<ReportKeyRow>();

    if (rows.results.length === 0) break;

    await deleteBodies(env, rows.results.map((row) => row.r2_key));
    await env.DB.batch(
      rows.results.map((row) =>
        env.DB.prepare("DELETE FROM reports WHERE install_id = ? AND day = ?").bind(row.install_id, row.day),
      ),
    );

    if (rows.results.length < BATCH_SIZE) break;
  }

  await env.DB.prepare("DELETE FROM requests WHERE utc_day < ?")
    .bind(utcDateString(-REQUEST_RETENTION_DAYS, now))
    .run();
}
