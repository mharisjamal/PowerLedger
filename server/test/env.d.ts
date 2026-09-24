declare namespace Cloudflare {
  interface Env {
    // Bound once R2 is enabled on the account (wrangler.toml's r2_buckets block, commented out
    // until then). Optional so the Worker runs on D1 alone until it is — see src/store.ts.
    REPORTS?: R2Bucket;
    DB: D1Database;
    ADDRESS_LIMIT: RateLimit;
    // A secret in production (`wrangler secret put ADMIN_TOKEN`), so it never lives in
    // wrangler.toml; set for tests in vitest.config.ts.
    ADMIN_TOKEN: string;
    // Test-only: the parsed migrations, set in vitest.config.ts and applied by
    // test/apply-migrations.ts.
    TEST_MIGRATIONS: import("cloudflare:test").D1Migration[];
  }
}
