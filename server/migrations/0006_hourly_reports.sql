-- Hourly sending (Plan Q spec §1): a report is either a complete day or today so far, replaced each hour until the
-- complete day replaces it. Rows from before this are complete days.
ALTER TABLE reports ADD COLUMN complete INTEGER NOT NULL DEFAULT 1;

-- Each kind of request gets its own daily count, so a day of hourly reports never uses up the budget a consent or a
-- delete needs: `count` stays the reports', `history` is /v1/history's and `control` is /v1/consent's and /v1/delete's.
ALTER TABLE requests ADD COLUMN history INTEGER NOT NULL DEFAULT 0;
ALTER TABLE requests ADD COLUMN control INTEGER NOT NULL DEFAULT 0;
