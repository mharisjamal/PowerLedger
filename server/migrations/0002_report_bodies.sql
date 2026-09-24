CREATE TABLE report_bodies (
  r2_key      TEXT PRIMARY KEY,                -- same key a bound R2 would use, e.g. reports/v1/<installId>/<day>.json.gz
  body        BLOB NOT NULL,                   -- the report as sent (gzip); D1 rows are capped at 2 MB, bodies are <= 1 MB
  content_type TEXT NOT NULL,
  received_at INTEGER NOT NULL                 -- ms, epoch
);
