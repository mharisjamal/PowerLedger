import { utcDateString } from "../day";
import { deleteBodies } from "../store";
import { APPROVED_REQUEST_LIFETIME_MS, JOIN_REQUEST_LIFETIME_MS, RECOVER_RETRY_MS } from "./account";
import { TIME_WINDOW_SECONDS } from "./auth";
import { MEETING_LIFETIME_MS } from "./meetings";

const BATCH_RETENTION_MS = 90 * 24 * 60 * 60 * 1000;
const REQUEST_RETENTION_DAYS = 2;
/** A signature is only worth remembering while its request's time could still be taken: 5 minutes either side. */
const SEEN_RETENTION_MS = 2 * TIME_WINDOW_SECONDS * 1000;
const BATCH_SIZE = 1000;
/** How long a run keeps working through a backlog before leaving the rest to the next day's. */
export const RETENTION_BUDGET_MS = 20_000;

export interface RetentionOptions {
  /** Rows a round; 1000 by default. */
  batchSize?: number;
  /** Time for the rounds; 20 s by default. */
  budgetMs?: number;
  /** The clock the budget is kept by; Date.now by default. */
  clock?: () => number;
}

/** Runs one part of the cron, so a part that fails is logged and the rest still run. */
export async function retentionPart(name: string, work: () => Promise<unknown>): Promise<void> {
  try {
    await work();
  } catch (error) {
    console.error(`Retention: ${name} failed.`, error);
  }
}

/**
 * Deletes, a round of `batchSize` at a time, whatever `select` gives until it gives fewer than a round or the time is up.
 * `drop` takes a round's rows away. No count of rounds limits a backlog, only the time.
 */
export async function workThrough<Row>(
  select: (limit: number) => Promise<Row[]>,
  drop: (rows: Row[]) => Promise<void>,
  options: RetentionOptions,
  deadline: number,
): Promise<void> {
  const clock = options.clock ?? Date.now;
  const batchSize = options.batchSize ?? BATCH_SIZE;
  while (clock() < deadline) {
    const rows = await select(batchSize);
    if (rows.length === 0) return;
    await drop(rows);
    if (rows.length < batchSize) return;
  }
}

interface BatchKeyRow {
  household: string;
  seq: number;
  r2_key: string;
}

/**
 * The households' part of the daily cron (households design §5, §8): batches and their bodies past 90 days, worked
 * through a round at a time for as long as the budget lasts, then meetings past their 10 minutes, join requests waiting
 * past 24 hours or approved more than 7 days ago, recovers remembered past the 10 minutes a retry is answered in, per-PC
 * request counts and daily totals past 2 days, and seen signatures past the time window. Each is a part of its own: one
 * failing doesn't stop the others.
 */
export async function runHouseholdRetention(
  env: Cloudflare.Env,
  now: Date,
  options: RetentionOptions = {},
  deadline = (options.clock ?? Date.now)() + (options.budgetMs ?? RETENTION_BUDGET_MS),
): Promise<void> {
  const batchCutoff = now.getTime() - BATCH_RETENTION_MS;
  await retentionPart("household batches", () =>
    workThrough<BatchKeyRow>(
      async (limit) =>
        (await env.DB.prepare("SELECT household, seq, r2_key FROM batches WHERE received < ? LIMIT ?")
          .bind(batchCutoff, limit)
          .all<BatchKeyRow>()).results,
      async (rows) => {
        await deleteBodies(env, rows.map((row) => row.r2_key));
        await env.DB.prepare(
          `DELETE FROM batches WHERE (household, seq) IN
             (SELECT json_extract(value, '$[0]'), json_extract(value, '$[1]') FROM json_each(?))`,
        )
          .bind(JSON.stringify(rows.map((row) => [row.household, row.seq])))
          .run();
      },
      options,
      deadline,
    ),
  );

  const statements: [string, D1PreparedStatement][] = [
    ["meetings", env.DB.prepare("DELETE FROM meetings WHERE created <= ?").bind(now.getTime() - MEETING_LIFETIME_MS)],
    [
      "join requests",
      env.DB.prepare(
        `DELETE FROM join_requests
         WHERE (approved_epoch IS NULL AND created <= ?1) OR (approved_epoch IS NOT NULL AND approved_at <= ?2)`,
      ).bind(now.getTime() - JOIN_REQUEST_LIFETIME_MS, now.getTime() - APPROVED_REQUEST_LIFETIME_MS),
    ],
    ["used recoveries", env.DB.prepare("DELETE FROM used_recoveries WHERE used <= ?").bind(now.getTime() - RECOVER_RETRY_MS)],
    ["per-PC counts", env.DB.prepare("DELETE FROM device_requests WHERE utc_day < ?").bind(utcDateString(-REQUEST_RETENTION_DAYS, now))],
    ["daily totals", env.DB.prepare("DELETE FROM daily_totals WHERE utc_day < ?").bind(utcDateString(-REQUEST_RETENTION_DAYS, now))],
    ["seen signatures", env.DB.prepare("DELETE FROM seen_signatures WHERE seen < ?").bind(now.getTime() - SEEN_RETENTION_MS)],
  ];
  for (const [name, statement] of statements) await retentionPart(name, () => statement.run());
}
