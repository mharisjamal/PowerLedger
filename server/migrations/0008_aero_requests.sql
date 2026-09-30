-- Aero by request (src/aero.ts, 0.10.7): a PC asks for the Aero look under a random id it made, AERO-XXXXX, and the owner
-- approves or revokes it by that id (tools/aero.mjs). Times are UTC ms; a revoke keeps approved_at, and a later approve
-- clears revoked_at.
CREATE TABLE aero_requests (
  id           TEXT PRIMARY KEY,               -- AERO- and 5 Crockford base32 characters
  name         TEXT NOT NULL,                  -- the PC's display name, at most 64 characters
  requested_at INTEGER NOT NULL,
  approved_at  INTEGER,
  revoked_at   INTEGER
);
