ALTER TABLE network_scan_runs
    ADD COLUMN target_addresses TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN force_identification BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN progress JSONB NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN progress_at_utc TIMESTAMPTZ;

ALTER TABLE network_assets
    ADD COLUMN response_time_ms DOUBLE PRECISION CHECK (response_time_ms >= 0),
    ADD COLUMN hostname_source VARCHAR(32),
    ADD COLUMN mac_source VARCHAR(32),
    ADD COLUMN mac_checked_at_utc TIMESTAMPTZ;

