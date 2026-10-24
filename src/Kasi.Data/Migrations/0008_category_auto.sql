-- M2.11b2: a job whose category was only the fallback (nothing in the link decided it) may be re-routed once, after the probe, by the
-- MIME type the server reports (TRD 10.4, rule 3). 0 for a job whose category a person named or a rule chose.
ALTER TABLE job ADD COLUMN category_auto INTEGER NOT NULL DEFAULT 0;
