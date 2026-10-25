-- KASI-QUE-007: bytes consumed per queue and quota period. period_start is a UTC ISO-8601 instant; the Scheduling layer computes
-- local midnight / local Monday 00:00, so a time-zone change does not rewrite history. QuotaReset.Never uses the Unix epoch.
CREATE TABLE queue_usage (
    queue_id INTEGER NOT NULL REFERENCES queue(id) ON DELETE CASCADE,
    period_start TEXT NOT NULL,
    bytes INTEGER NOT NULL DEFAULT 0 CHECK (bytes >= 0),
    PRIMARY KEY (queue_id, period_start)
);
