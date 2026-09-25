-- History for new users (Plan Q spec §2): the hourly totals already on a PC when its user agrees on consent version 2
-- with power on, sent once in chunks of at most 31 days. Bodies are kept like report bodies (src/store.ts), at
-- history/v1/<installId>/<from_ms>.json.gz; retention drops a chunk 3 years after its last hour.
CREATE TABLE histories (
  install_id  TEXT NOT NULL,
  from_ms     INTEGER NOT NULL,              -- the first hour's start, UTC ms
  to_ms       INTEGER NOT NULL,              -- the last hour's end, UTC ms
  received_at INTEGER NOT NULL,              -- ms
  bytes       INTEGER NOT NULL,              -- as sent, compressed
  country     TEXT NOT NULL,
  r2_key      TEXT NOT NULL,
  PRIMARY KEY (install_id, from_ms)
);
CREATE INDEX histories_from ON histories(from_ms);
CREATE INDEX histories_to ON histories(to_ms);
