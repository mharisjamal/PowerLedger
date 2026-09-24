import path from "node:path";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

// Config shape follows @cloudflare/vitest-pool-workers 0.22.0's own docs: the pool is now a Vite
// plugin (`cloudflareTest`) rather than `defineWorkersProject`/`poolOptions.workers`, and
// `isolatedStorage`/`singleWorker` no longer exist (removed in favour of per-test-file storage
// isolation, per the package's own changelog). Tests are written to keep any request sequence
// that depends on earlier state inside a single `it()`, so that per-file model is enough.
export default defineConfig(async () => {
  const migrations = await readD1Migrations(path.join(import.meta.dirname, "migrations"));

  return {
    plugins: [
      cloudflareTest({
        wrangler: { configPath: "./wrangler.toml" },
        miniflare: {
          bindings: {
            TEST_MIGRATIONS: migrations,
            ADMIN_TOKEN: "test-admin-token",
            // Sign-in's client IDs, as the tests' own ID tokens name them.
            MS_CLIENT_ID: "11111111-2222-3333-4444-555555555555",
            GOOGLE_CLIENT_ID: "test-client.apps.googleusercontent.com",
            // Feedback's repo and token: the tests never reach GitHub (a fake fetch is injected), only
            // the checks that both are set.
            FEEDBACK_REPO: "owner/feedback-test",
            FEEDBACK_GITHUB_TOKEN: "test-github-token",
          },
          // Independent of wrangler.toml, whose r2_buckets block is commented out until R2 is
          // enabled on the account: the test Worker keeps an R2 bucket bound as REPORTS, so the
          // R2 code paths (as opposed to the D1-fallback ones, exercised with REPORTS forced to
          // undefined) stay covered in the meantime.
          r2Buckets: { REPORTS: "powerledger-data" },
        },
      }),
    ],
    test: {
      // Only the Worker's own tests: tools/export.test.mjs runs under plain Node (node:test,
      // package.json's "test" script), not inside the Workers runtime this project sets up.
      include: ["test/**/*.test.ts"],
      setupFiles: ["./test/apply-migrations.ts"],
    },
  };
});
