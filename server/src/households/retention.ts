import { utcDateString } from "../day";
import { deleteBodies } from "../store";
import { JOIN_REQUEST_LIFETIME_MS } from "./account";
import { TIME_WINDOW_SECONDS } from "./auth";
import { MEETING_LIFETIME_MS } from "./meetings";

const BATCH_RETENTION_MS = 90 * 24 * 60 * 60 * 1000;
const REQUEST_RETENTION_DAYS = 2;
/** A signature is only worth remembering while its request's time could still be taken: 5 minutes either side. */
const SEEN_RETENTION_MS = 2 * TIME_WINDOW_SECONDS * 1000;
const BATCH_SIZE = 1000;
const MAX_BATCHES = 10;

interface BatchKeyRow {
  household: string;
  seq: number;
  r2_key: string;
}

/**
 * The households' part of the daily cron (households design §5, §8): batches and their bodies past 90 days, meetings
 * past their 10 minutes, join requests past 7 days, per-PC request counts past 2 days, and seen signatures past the
 * time window. Batches are worked
 * off 1000 at a time, at most 10 batches a run, like reports.
 */
export async function runHouseholdRetention(env: Cloudflare.Env, now: Date): Promise<void> {
  const batchCutoff = now.getTime() - BATCH_RETENTION_MS;
  for (let round = 0; round < MAX_BATCHES; round++) {
    const rows = await env.DB.prepare("SELECT household, seq, r2_key FROM batches WHERE received < ? LIMIT ?")
      .bind(batchCutoff, BATCH_SIZE)
      .all<BatchKeyRow>();
    if (rows.results.length === 0) break;

    await deleteBodies(env, rows.results.map((row) => row.r2_key));
    await env.DB.batch(
      rows.results.map((row) =>
        env.DB.prepare("DELETE FROM batches WHERE household = ? AND seq = ?").bind(row.household, row.seq),
      ),
    );

    if (rows.results.length < BATCH_SIZE) break;
  }

  await env.DB.batch([
    env.DB.prepare("DELETE FROM meetings WHERE created <= ?").bind(now.getTime() - MEETING_LIFETIME_MS),
    env.DB.prepare("DELETE FROM join_requests WHERE created <= ?").bind(now.getTime() - JOIN_REQUEST_LIFETIME_MS),
    env.DB.prepare("DELETE FROM device_requests WHERE utc_day < ?").bind(utcDateString(-REQUEST_RETENTION_DAYS, now)),
    env.DB.prepare("DELETE FROM daily_totals WHERE utc_day < ?").bind(utcDateString(-REQUEST_RETENTION_DAYS, now)),
    env.DB.prepare("DELETE FROM seen_signatures WHERE seen < ?").bind(now.getTime() - SEEN_RETENTION_MS),
  ]);
}
