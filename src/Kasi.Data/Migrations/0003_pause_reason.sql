-- Why a paused job is paused. NULL in every other state; a restart auto-resumes only NetworkOffline (M1.13, TRD 6.9).
ALTER TABLE job ADD COLUMN pause_reason TEXT;
