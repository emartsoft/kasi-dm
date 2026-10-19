-- Changed-file restarts survive an app restart, so the "third change fails" cap cannot be bypassed (M1.13c, KASI-RES-003).
ALTER TABLE job ADD COLUMN restart_count INTEGER NOT NULL DEFAULT 0;
