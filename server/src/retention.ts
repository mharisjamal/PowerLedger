import { utcDateString, utcDateYearsAgo } from "./day";
import { utcHour } from "./feedback";
import { RETENTION_BUDGET_MS, type RetentionOptions, retentionPart, runHouseholdRetention, workThrough } from "./households/retention";
import { deleteBodies } from "./store";

const RETENTION_YEARS = 3;
const REQUEST_RETENTION_DAYS = 2;

interface ReportKeyRow {
  install_id: string;
  day: string;
  r2_key: string;
}

interface HistoryKeyRow {
  install_id: string;
  from_ms: number;
  r2_key: string;
}

/**
 * The daily cron: deletes reports (and their R2 objects) whose day is more than 3 years old,
 * history chunks whose last hour ended more than 3 years ago, request counts more than 2 days old and feedback's address counts for the hours that have
 * passed; then the households' own (households/retention.ts). Old
 * reports are worked through 1000 at a time for as long as the run's 20 s budget lasts, so a large
 * backlog simply continues on the next run. Each part is on its own: one failing doesn't stop the rest.
 */
export async function runRetention(env: Cloudflare.Env, now: Date = new Date(), options: RetentionOptions = {}): Promise<void> {
  const deadline = (options.clock ?? Date.now)() + (options.budgetMs ?? RETENTION_BUDGET_MS);
  const cutoff = utcDateYearsAgo(RETENTION_YEARS, now);

  await retentionPart("reports", () =>
    workThrough<ReportKeyRow>(
      async (limit) =>
        (await env.DB.prepare("SELECT install_id, day, r2_key FROM reports WHERE day < ? LIMIT ?")
          .bind(cutoff, limit)
          .all<ReportKeyRow>()).results,
      async (rows) => {
        await deleteBodies(env, rows.map((row) => row.r2_key));
        await env.DB.prepare(
          `DELETE FROM reports WHERE (install_id, day) IN
             (SELECT json_extract(value, '$[0]'), json_extract(value, '$[1]') FROM json_each(?))`,
        )
          .bind(JSON.stringify(rows.map((row) => [row.install_id, row.day])))
          .run();
      },
      options,
      deadline,
    ),
  );

  // History goes 3 years after its last hour: the chunk's end, from the same cutoff day's start.
  const historyCutoffMs = Date.parse(`${cutoff}T00:00:00Z`);
  await retentionPart("history", () =>
    workThrough<HistoryKeyRow>(
      async (limit) =>
        (await env.DB.prepare("SELECT install_id, from_ms, r2_key FROM histories WHERE to_ms < ? LIMIT ?")
          .bind(historyCutoffMs, limit)
          .all<HistoryKeyRow>()).results,
      async (rows) => {
        await deleteBodies(env, rows.map((row) => row.r2_key));
        await env.DB.prepare(
          `DELETE FROM histories WHERE (install_id, from_ms) IN
             (SELECT json_extract(value, '$[0]'), json_extract(value, '$[1]') FROM json_each(?))`,
        )
          .bind(JSON.stringify(rows.map((row) => [row.install_id, row.from_ms])))
          .run();
      },
      options,
      deadline,
    ),
  );

  await retentionPart("request counts", () =>
    env.DB.prepare("DELETE FROM requests WHERE utc_day < ?").bind(utcDateString(-REQUEST_RETENTION_DAYS, now)).run(),
  );
  await retentionPart("feedback counts", () =>
    env.DB.prepare("DELETE FROM feedback_addresses WHERE utc_hour < ?").bind(utcHour(now.getTime())).run(),
  );

  await runHouseholdRetention(env, now, options, deadline);
}
