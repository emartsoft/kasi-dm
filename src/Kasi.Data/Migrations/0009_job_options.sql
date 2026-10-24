-- UIR-3b: per-job choices made in the Active Transfer window. 1 opens the file with its program when the job completes (the speed limit
-- already has its column, speed_limit_bps).
ALTER TABLE job ADD COLUMN open_when_completed INTEGER NOT NULL DEFAULT 0;
