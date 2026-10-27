-- M4a speed profiles (KASI-BWL-001/003). The active profile is kept in the existing setting table so export/import carries it.
CREATE TABLE speed_profile (
    id         INTEGER PRIMARY KEY,
    name       TEXT    NOT NULL UNIQUE,
    limit_bps  INTEGER CHECK (limit_bps IS NULL OR limit_bps > 0),
    sort_order INTEGER NOT NULL DEFAULT 0
);

ALTER TABLE queue_schedule ADD COLUMN profile_id INTEGER REFERENCES speed_profile(id) ON DELETE SET NULL;
