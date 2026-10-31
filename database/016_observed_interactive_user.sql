ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS interactive_user_account VARCHAR(320),
    ADD COLUMN IF NOT EXISTS interactive_user_display_name VARCHAR(256),
    ADD COLUMN IF NOT EXISTS interactive_user_sid VARCHAR(184),
    ADD COLUMN IF NOT EXISTS interactive_user_session_type VARCHAR(32),
    ADD COLUMN IF NOT EXISTS interactive_user_resolution_source VARCHAR(32),
    ADD COLUMN IF NOT EXISTS interactive_user_checked_at_utc TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS ix_devices_interactive_user_account
    ON devices (interactive_user_account)
    WHERE interactive_user_account IS NOT NULL;
