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
          },
        },
      }),
    ],
    test: {
      setupFiles: ["./test/apply-migrations.ts"],
    },
  };
});
