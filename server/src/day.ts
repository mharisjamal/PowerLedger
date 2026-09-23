/** `now`, shifted by `offsetDays` whole UTC days, as `yyyy-MM-dd`. */
export function utcDateString(offsetDays: number, now: Date): string {
  const shifted = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + offsetDays));
  return shifted.toISOString().slice(0, 10);
}

/** Whether `day` (yyyy-MM-dd) falls between 15 days back and tomorrow, inclusive, in UTC. */
export function dayInRange(day: string, now: Date): boolean {
  return day >= utcDateString(-15, now) && day <= utcDateString(1, now);
}

/** `now`, shifted back `years` whole calendar years (so leap days land correctly), as `yyyy-MM-dd`. */
export function utcDateYearsAgo(years: number, now: Date): string {
  const shifted = new Date(Date.UTC(now.getUTCFullYear() - years, now.getUTCMonth(), now.getUTCDate()));
  return shifted.toISOString().slice(0, 10);
}
