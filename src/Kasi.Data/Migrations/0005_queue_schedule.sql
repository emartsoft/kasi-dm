-- Queue schedules (M2.10, TRD 10.2).
CREATE TABLE queue_schedule (
    queue_id        INTEGER PRIMARY KEY REFERENCES queue(id) ON DELETE CASCADE,
    is_enabled      INTEGER NOT NULL DEFAULT 0,
    start_time      TEXT NOT NULL,              -- HH:mm:ss
    stop_time       TEXT,                       -- HH:mm:ss, NULL = until queue empty
    days_of_week    INTEGER NOT NULL DEFAULT 127 -- bitmask (1=Sun, 2=Mon, 4=Tue, 8=Wed, 16=Thu, 32=Fri, 64=Sat)
);
