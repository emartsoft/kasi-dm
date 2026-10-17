-- Settings live in the same database so export and import is one file (TRD 11).
CREATE TABLE setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
) WITHOUT ROWID;

-- The main queue (TRD 10.1). Connections and speed limit stay NULL: the global defaults apply.
INSERT INTO queue (id, name, max_parallel, sort_order) VALUES (1, 'Main', 4, 0);

-- Default categories (TRD 10.4) with fixed ids. Folders are relative to the user's Downloads folder, which the
-- application resolves at run time; General is the fallback and saves straight into Downloads.
INSERT INTO category (id, name, folder, is_builtin) VALUES
    (1, 'General',    '',           1),
    (2, 'Compressed', 'Compressed', 1),
    (3, 'Documents',  'Documents',  1),
    (4, 'Music',      'Music',      1),
    (5, 'Video',      'Video',      1),
    (6, 'Programs',   'Programs',   1),
    (7, 'Images',     'Images',     1),
    (8, 'Torrents',   'Torrents',   1);
