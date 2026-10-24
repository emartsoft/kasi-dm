-- Category rules (M2.11, TRD 10.4, 11.1, KASI-CAT-001 and 002).
-- kind: 'extension' (pattern is the extension without its dot), 'domain' (a glob matched against the host) or 'url' (a glob matched against the
-- address without query and fragment). Within a kind a lower position is tried first. Built-in rules can be switched off, not deleted.
CREATE TABLE category_rule (
    id          INTEGER PRIMARY KEY,
    category_id INTEGER NOT NULL REFERENCES category(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL CHECK (kind IN ('extension','domain','url')),
    pattern     TEXT NOT NULL CHECK (length(pattern) BETWEEN 1 AND 256),
    position    INTEGER NOT NULL DEFAULT 0,
    is_builtin  INTEGER NOT NULL DEFAULT 0,
    is_enabled  INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX ix_category_rule_category ON category_rule(category_id);

-- The default extension rules: the table of TRD 10.4, plus the extra archive and installer types the browser already hands over
-- (bz2, tar.gz, deb, rpm, apk, dmg, pkg) and jpeg. Category ids are the built-in rows of migration 0002.
INSERT INTO category_rule (category_id, kind, pattern, position, is_builtin) VALUES
    (2, 'extension', 'zip',    10, 1), (2, 'extension', 'rar',    11, 1), (2, 'extension', '7z',     12, 1),
    (2, 'extension', 'tar',    13, 1), (2, 'extension', 'gz',     14, 1), (2, 'extension', 'xz',     15, 1),
    (2, 'extension', 'bz2',    16, 1), (2, 'extension', 'tar.gz', 17, 1),
    (3, 'extension', 'pdf',    20, 1), (3, 'extension', 'doc',    21, 1), (3, 'extension', 'docx',   22, 1),
    (3, 'extension', 'xls',    23, 1), (3, 'extension', 'xlsx',   24, 1), (3, 'extension', 'ppt',    25, 1),
    (3, 'extension', 'pptx',   26, 1), (3, 'extension', 'txt',    27, 1), (3, 'extension', 'epub',   28, 1),
    (4, 'extension', 'mp3',    30, 1), (4, 'extension', 'm4a',    31, 1), (4, 'extension', 'flac',   32, 1),
    (4, 'extension', 'wav',    33, 1), (4, 'extension', 'ogg',    34, 1), (4, 'extension', 'opus',   35, 1),
    (5, 'extension', 'mp4',    40, 1), (5, 'extension', 'mkv',    41, 1), (5, 'extension', 'webm',   42, 1),
    (5, 'extension', 'mov',    43, 1), (5, 'extension', 'avi',    44, 1),
    (6, 'extension', 'exe',    50, 1), (6, 'extension', 'msi',    51, 1), (6, 'extension', 'msix',   52, 1),
    (6, 'extension', 'appx',   53, 1), (6, 'extension', 'iso',    54, 1), (6, 'extension', 'img',    55, 1),
    (6, 'extension', 'deb',    56, 1), (6, 'extension', 'rpm',    57, 1), (6, 'extension', 'apk',    58, 1),
    (6, 'extension', 'dmg',    59, 1), (6, 'extension', 'pkg',    60, 1),
    (7, 'extension', 'jpg',    70, 1), (7, 'extension', 'jpeg',   71, 1), (7, 'extension', 'png',    72, 1),
    (7, 'extension', 'webp',   73, 1), (7, 'extension', 'gif',    74, 1), (7, 'extension', 'svg',    75, 1);
