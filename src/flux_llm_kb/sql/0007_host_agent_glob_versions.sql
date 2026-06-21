ALTER TABLE monitored_roots
    ADD COLUMN IF NOT EXISTS glob_mode text NOT NULL DEFAULT 'extend';

ALTER TABLE monitored_roots
    ADD CONSTRAINT monitored_roots_glob_mode_check
    CHECK (glob_mode IN ('inherit', 'extend', 'override')) NOT VALID;

CREATE INDEX IF NOT EXISTS idx_monitored_roots_glob_mode
    ON monitored_roots (glob_mode);
