-- In-app feedback (src/feedback.ts): how many pieces each address (an IPv6 one by its /64) sent in each UTC hour, so one
-- address files at most 10 an hour. The daily cron drops the hours that have passed.
CREATE TABLE feedback_addresses (
  address  TEXT NOT NULL,
  utc_hour TEXT NOT NULL,                     -- yyyy-MM-ddTHH, UTC
  count    INTEGER NOT NULL,
  PRIMARY KEY (address, utc_hour)
);
