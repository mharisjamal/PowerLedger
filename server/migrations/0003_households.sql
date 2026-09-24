-- Households (households design §8): membership, public keys and sealed batches. Never a household key, a PC's
-- name or any row: names and rows travel inside the batches, sealed with a key the server never sees.
CREATE TABLE households (
  id      TEXT PRIMARY KEY,                   -- 32 lower-case hex, made by the PC that creates the household
  created INTEGER NOT NULL,                   -- ms
  epoch   INTEGER NOT NULL DEFAULT 1,         -- the household key's current epoch: new keys are posted for epoch + 1 only
  next_seq INTEGER NOT NULL DEFAULT 1         -- the next batch number: taken with each batch, never reset by retention
);
CREATE TABLE members (
  household TEXT NOT NULL,
  device    TEXT NOT NULL,                    -- 32 hex: the first 16 bytes of SHA-256 of sign_key's SPKI
  sign_key  TEXT NOT NULL,                    -- ECDSA P-256 SubjectPublicKeyInfo, base64url
  dh_key    TEXT NOT NULL,                    -- ECDH P-256 SubjectPublicKeyInfo, base64url
  added     INTEGER NOT NULL,                 -- ms, for display: membership is ordered by the epochs below
  removed   INTEGER,                          -- ms; NULL while a current member
  added_epoch   INTEGER NOT NULL DEFAULT 1,   -- the household's epoch when this PC was (last) added
  removed_epoch INTEGER,                      -- the household's epoch when it was removed; NULL while current
  PRIMARY KEY (household, device)
);
CREATE INDEX members_device ON members(device);
CREATE TABLE batches (
  household  TEXT NOT NULL,
  seq        INTEGER NOT NULL,                -- the household's order, given by the Worker (max + 1): the fetch cursor
  device     TEXT NOT NULL,                   -- who sent it
  epoch      INTEGER NOT NULL,
  device_seq INTEGER NOT NULL,                -- the sender's own sequence number, part of the batch's associated data
  bytes      INTEGER NOT NULL,                -- the sealed body's size
  received   INTEGER NOT NULL,                -- ms
  r2_key     TEXT NOT NULL,                   -- where store.ts keeps the sealed body
  sig        TEXT NOT NULL,                   -- the sender's signature over BatchToSign, base64url: kept and handed on, checked by members
  PRIMARY KEY (household, seq)
);
CREATE INDEX batches_received ON batches(received);
CREATE TABLE key_envelopes (
  household   TEXT NOT NULL,
  epoch       INTEGER NOT NULL,
  device      TEXT NOT NULL,                  -- who it is for
  from_device TEXT NOT NULL,                  -- who sealed it: opening it takes that member's dh_key
  body        TEXT NOT NULL,                  -- base64url, as sent
  created     INTEGER NOT NULL,               -- ms
  PRIMARY KEY (household, epoch, device)
);
CREATE TABLE meetings (
  id      TEXT NOT NULL,                      -- 32 hex: the first 16 bytes of SHA-256 of the pairing code
  slot    TEXT NOT NULL,                      -- adder, joiner, answer or welcome
  body    BLOB NOT NULL,                      -- as sent, at most 8 KB
  created INTEGER NOT NULL,                   -- ms of the meeting's first PUT: every slot ends 10 minutes after it
  PRIMARY KEY (id, slot)
);
CREATE INDEX meetings_created ON meetings(created);
CREATE TABLE device_requests (
  device      TEXT NOT NULL,
  utc_day     TEXT NOT NULL,                  -- yyyy-MM-dd
  count       INTEGER NOT NULL,               -- signed requests taken
  batches     INTEGER NOT NULL DEFAULT 0,     -- batches taken
  batch_bytes INTEGER NOT NULL DEFAULT 0,     -- their sealed bytes
  PRIMARY KEY (device, utc_day)
);
CREATE TABLE daily_totals (                   -- what the whole server took in a UTC day, against its safety cap
  utc_day     TEXT PRIMARY KEY,
  batch_bytes INTEGER NOT NULL
);
CREATE TABLE seen_signatures (                -- signed requests already taken, so none is taken twice
  device TEXT NOT NULL,
  r      TEXT NOT NULL,                       -- hex of the signature's r: new for every signature, and kept when s is flipped
  seen   INTEGER NOT NULL,                    -- ms
  PRIMARY KEY (device, r)
);
CREATE INDEX seen_signatures_seen ON seen_signatures(seen);
