-- Optional sign-in (households design §7): only the provider and its subject ID are kept, never an e-mail address. An
-- account links to one household; PCs signed in as it join by a member's approval, or with the recovery code.
CREATE TABLE accounts (
  id       TEXT PRIMARY KEY,                  -- random, the Worker's own
  provider TEXT NOT NULL,                     -- microsoft or google
  subject  TEXT NOT NULL,                     -- the ID token's sub
  created  INTEGER NOT NULL,                  -- ms
  UNIQUE (provider, subject)
);
CREATE TABLE sessions (
  token_hash TEXT PRIMARY KEY,                -- SHA-256 hex of the session token (32 random bytes, base64url)
  account    TEXT NOT NULL,
  device     TEXT NOT NULL,                   -- one session per PC
  sign_key   TEXT NOT NULL,                   -- the PC's keys as it signed in: its account requests are signed by this one
  dh_key     TEXT NOT NULL,
  created    INTEGER NOT NULL                 -- ms
);
CREATE INDEX sessions_account ON sessions(account);
CREATE INDEX sessions_device ON sessions(device);
CREATE TABLE account_households (
  account   TEXT PRIMARY KEY,                 -- an account links to one household
  household TEXT NOT NULL,
  linked    INTEGER NOT NULL                  -- ms
);
CREATE INDEX account_households_household ON account_households(household);
CREATE TABLE join_requests (                  -- PCs signed in as a linked account, waiting for a member to approve them
  household TEXT NOT NULL,
  device    TEXT NOT NULL,
  account   TEXT NOT NULL,
  sign_key  TEXT NOT NULL,
  dh_key    TEXT NOT NULL,
  created   INTEGER NOT NULL,                 -- ms; a request lasts 24 hours
  approver  TEXT,                             -- the member that committed, the only one that may reveal and approve
  commitment TEXT,                            -- its commitment to its nonce, base64url; each of these four written once
  nonce     TEXT,                             -- the waiting PC's nonce, base64url
  reveal    TEXT,                             -- the approver's nonce, revealed, base64url
  approved_epoch INTEGER,                     -- set by the approval; the request then stays until the PC reads it
  PRIMARY KEY (household, device)
);
CREATE INDEX join_requests_account ON join_requests(account);
CREATE TABLE recovery (
  account       TEXT PRIMARY KEY,
  body          TEXT NOT NULL,                -- the key and member list sealed under the recovery code's key, base64url, as sent
  verifier_hash TEXT NOT NULL,                -- hex SHA-256 of the 32-byte verifier recover must send; never the verifier
  epoch         INTEGER NOT NULL,             -- the household's epoch the body was sealed at (its current one when put)
  holder        TEXT NOT NULL,                -- the one PC that holds the code, and alone puts the recovery
  updated       INTEGER NOT NULL              -- ms
);
