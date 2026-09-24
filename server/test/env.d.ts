declare namespace Cloudflare {
  interface Env {
    // Bound once R2 is enabled on the account (wrangler.toml's r2_buckets block, commented out
    // until then). Optional so the Worker runs on D1 alone until it is — see src/store.ts.
    REPORTS?: R2Bucket;
    DB: D1Database;
    ADDRESS_LIMIT: RateLimit;
    // Starting a pairing-by-code meeting, per address (wrangler.toml).
    MEETING_LIMIT: RateLimit;
    // A secret in production (`wrangler secret put ADMIN_TOKEN`), so it never lives in
    // wrangler.toml; set for tests in vitest.config.ts.
    ADMIN_TOKEN: string;
    // Sign-in's public client IDs (wrangler.toml's [vars]): the owner's Azure app registration and
    // Google OAuth client. Empty turns that provider's sign-in off; tests set their own.
    MS_CLIENT_ID?: string;
    GOOGLE_CLIENT_ID?: string;
    // In-app feedback (src/feedback.ts): the owner's private repo, "owner/name" (wrangler.toml's
    // [vars]), and a fine-grained token for it (`wrangler secret put FEEDBACK_GITHUB_TOKEN`, by
    // tools/set-feedback-token.ps1). Either empty turns feedback off (503).
    FEEDBACK_REPO?: string;
    FEEDBACK_GITHUB_TOKEN?: string;
    // Test-only: the parsed migrations, set in vitest.config.ts and applied by
    // test/apply-migrations.ts.
    TEST_MIGRATIONS: import("cloudflare:test").D1Migration[];
  }
}
