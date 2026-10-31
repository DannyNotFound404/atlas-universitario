ALTER TABLE devices ADD COLUMN IF NOT EXISTS updater_heartbeat_received_at_utc timestamptz;

CREATE TABLE updater_release_policies (
    release_id uuid PRIMARY KEY,
    policy jsonb NOT NULL,
    revision integer NOT NULL DEFAULT 1,
    paused_automatically boolean NOT NULL DEFAULT false,
    pause_reason varchar(120),
    failure_window_start timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_by varchar(160) NOT NULL
);
CREATE TABLE updater_controls (
    authorization_id uuid PRIMARY KEY,
    purpose varchar(32) NOT NULL CHECK (purpose IN ('RECOVERY','TRUST_TRANSITION')),
    release_id uuid,
    generation bigint NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    envelope jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    created_by varchar(160) NOT NULL
);
CREATE UNIQUE INDEX ux_updater_trust_generation ON updater_controls(generation) WHERE purpose='TRUST_TRANSITION';
CREATE TABLE updater_attempts (
    attempt_id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES devices(id),
    release_id uuid NOT NULL,
    lease_id uuid NOT NULL,
    lease_expires_at_utc timestamptz NOT NULL,
    phase varchar(40) NOT NULL DEFAULT 'LEASED',
    code varchar(120) NOT NULL DEFAULT 'lease_granted',
    sequence bigint NOT NULL DEFAULT 0,
    updater_version varchar(64) NOT NULL,
    components jsonb NOT NULL DEFAULT '[]',
    downloaded_bytes bigint NOT NULL DEFAULT 0,
    total_bytes bigint NOT NULL DEFAULT 0,
    agent_changed boolean NOT NULL DEFAULT false,
    changed_components jsonb NOT NULL DEFAULT '[]',
    install_authorized_at_utc timestamptz,
    baseline_agent_started_at_utc timestamptz,
    awaiting_confirmation_at_utc timestamptz,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    updated_at_utc timestamptz NOT NULL DEFAULT now(),
    confirmed_at_utc timestamptz
);
CREATE INDEX ix_updater_attempts_device ON updater_attempts(device_id,created_at_utc DESC);
CREATE INDEX ix_updater_attempts_release ON updater_attempts(release_id,created_at_utc DESC);
CREATE INDEX ix_updater_attempts_lease ON updater_attempts(lease_expires_at_utc);
CREATE TABLE updater_events (
    event_id uuid PRIMARY KEY,
    attempt_id uuid NOT NULL REFERENCES updater_attempts(attempt_id),
    device_id uuid NOT NULL REFERENCES devices(id),
    sequence bigint NOT NULL,
    phase varchar(40) NOT NULL,
    code varchar(120) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    received_at_utc timestamptz NOT NULL DEFAULT now(),
    payload jsonb NOT NULL,
    UNIQUE(attempt_id,sequence)
);
CREATE INDEX ix_updater_events_attempt ON updater_events(attempt_id,sequence);
CREATE INDEX ix_updater_events_device ON updater_events(device_id,received_at_utc DESC);

CREATE TABLE updater_devices (
    device_id uuid PRIMARY KEY REFERENCES devices(id),
    updater_version varchar(64) NOT NULL,
    components jsonb NOT NULL,
    retry_generation integer NOT NULL DEFAULT 0,
    last_check_at_utc timestamptz NOT NULL DEFAULT now()
);
