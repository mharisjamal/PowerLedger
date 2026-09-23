CREATE TABLE installs (
  id              TEXT PRIMARY KEY,
  key_hash        TEXT NOT NULL,             -- SHA-256 hex of the install key, set by the first request (trust on first use)
  first_seen      INTEGER NOT NULL,          -- ms
  last_seen       INTEGER NOT NULL,
  consent_version INTEGER NOT NULL,
  diagnostics     INTEGER NOT NULL,
  usage           INTEGER NOT NULL,
  power           INTEGER NOT NULL,
  share           INTEGER NOT NULL,
  country         TEXT,
  app_version     TEXT
);
CREATE TABLE reports (
  install_id  TEXT NOT NULL,
  day         TEXT NOT NULL,                  -- the PC's local day, yyyy-MM-dd
  received_at INTEGER NOT NULL,
  bytes       INTEGER NOT NULL,               -- as sent, compressed
  sections    TEXT NOT NULL,                  -- e.g. "diagnostics,power"
  country     TEXT NOT NULL,
  r2_key      TEXT NOT NULL,
  PRIMARY KEY (install_id, day)
);
CREATE INDEX reports_day ON reports(day);
CREATE TABLE requests (
  install_id TEXT NOT NULL,
  utc_day    TEXT NOT NULL,
  count      INTEGER NOT NULL,
  PRIMARY KEY (install_id, utc_day)
);
CREATE TABLE tombstones (
  id         TEXT PRIMARY KEY,
  deleted_at INTEGER NOT NULL
);
