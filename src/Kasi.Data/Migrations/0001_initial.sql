-- Core tables, copied from TRD 11.2 (KASI-TRD-2026-001).

CREATE TABLE queue (
    id              INTEGER PRIMARY KEY,
    name            TEXT    NOT NULL UNIQUE,
    max_parallel    INTEGER NOT NULL DEFAULT 4,
    connections     INTEGER,                    -- NULL = global default
    speed_limit_bps INTEGER,                    -- NULL = unlimited
    quota_bytes     INTEGER,
    quota_reset     TEXT CHECK (quota_reset IN ('daily','weekly','never')),
    post_action     TEXT NOT NULL DEFAULT 'none',
    sort_order      INTEGER NOT NULL
);

CREATE TABLE category (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    folder      TEXT NOT NULL,
    is_builtin  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE job (
    id              TEXT PRIMARY KEY,           -- UUIDv7, sortable by creation time
    kind            TEXT NOT NULL CHECK (kind IN ('http','ftp','media','torrent')),
    url             TEXT NOT NULL,
    final_url       TEXT,
    referrer        TEXT,
    file_name       TEXT NOT NULL,
    folder          TEXT NOT NULL,
    total_bytes     INTEGER,                    -- NULL = unknown
    done_bytes      INTEGER NOT NULL DEFAULT 0,
    state           TEXT NOT NULL,              -- see section 6.9
    error           TEXT,
    etag            TEXT,
    last_modified   TEXT,
    resumable       INTEGER NOT NULL DEFAULT 1,
    connections     INTEGER,
    speed_limit_bps INTEGER,
    checksum_algo   TEXT,
    checksum        TEXT,
    queue_id        INTEGER NOT NULL REFERENCES queue(id),
    queue_pos       REAL    NOT NULL,           -- fractional index for cheap reordering
    category_id     INTEGER REFERENCES category(id),
    created_utc     TEXT NOT NULL,
    completed_utc   TEXT
);
CREATE INDEX ix_job_queue ON job(queue_id, state, queue_pos);
CREATE INDEX ix_job_created ON job(created_utc DESC);

CREATE TABLE segment (
    job_id   TEXT    NOT NULL REFERENCES job(id) ON DELETE CASCADE,
    idx      INTEGER NOT NULL,
    start    INTEGER NOT NULL,
    "end"    INTEGER NOT NULL,                  -- exclusive
    written  INTEGER NOT NULL,                  -- bytes flushed to disk from start
    PRIMARY KEY (job_id, idx)
) WITHOUT ROWID;

CREATE TABLE job_secret (
    job_id   TEXT PRIMARY KEY REFERENCES job(id) ON DELETE CASCADE,
    payload  BLOB NOT NULL                      -- DPAPI-protected JSON: cookies, headers
);
