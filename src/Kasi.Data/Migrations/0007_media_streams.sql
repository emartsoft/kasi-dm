-- Media capture structure (M3.3/3c). Migration 0003 was already taken; 0007 is the next free number on main (0006 is the category rules).
CREATE TABLE media_stream (
    job_id          TEXT    NOT NULL REFERENCES job(id) ON DELETE CASCADE,
    idx             INTEGER NOT NULL,
    role            TEXT    NOT NULL CHECK (role IN ('video','audio','subtitle')),
    language        TEXT,
    codecs          TEXT,
    container       TEXT,
    variant_id      TEXT,
    estimated_bytes INTEGER,
    init_url        TEXT,
    init_size       INTEGER,
    init_range_start INTEGER,
    init_range_end  INTEGER,
    PRIMARY KEY (job_id, idx)
) WITHOUT ROWID;

CREATE TABLE media_segment (
    job_id          TEXT    NOT NULL,
    stream_idx      INTEGER NOT NULL,
    idx             INTEGER NOT NULL,
    url             TEXT    NOT NULL,
    size            INTEGER,
    completed       INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
    iv              TEXT,
    discontinuity   INTEGER NOT NULL DEFAULT 0 CHECK (discontinuity IN (0, 1)),
    range_start     INTEGER,
    range_end       INTEGER,
    PRIMARY KEY (job_id, stream_idx, idx),
    FOREIGN KEY (job_id, stream_idx) REFERENCES media_stream(job_id, idx) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE INDEX ix_media_segment_incomplete ON media_segment(job_id, stream_idx, completed);
