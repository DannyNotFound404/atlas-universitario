CREATE TABLE IF NOT EXISTS enrollment_tokens
(
    token_hash      BYTEA PRIMARY KEY,
    agency_code     VARCHAR(64) NOT NULL,
    created_at_utc  TIMESTAMPTZ NOT NULL,
    expires_at_utc  TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_enrollment_tokens_expiration
    ON enrollment_tokens (expires_at_utc);

CREATE TABLE IF NOT EXISTS devices
(
    id                      UUID PRIMARY KEY,
    installation_id         UUID NOT NULL UNIQUE,
    agency_code             VARCHAR(64) NOT NULL,
    machine_name            VARCHAR(255) NOT NULL,
    credential_hash         BYTEA NOT NULL,
    enabled                 BOOLEAN NOT NULL DEFAULT TRUE,
    agent_version           VARCHAR(64) NOT NULL,
    backup_client_version   VARCHAR(64),
    enrolled_at_utc         TIMESTAMPTZ NOT NULL,
    last_seen_at_utc        TIMESTAMPTZ NOT NULL,
    agent_started_at_utc    TIMESTAMPTZ,
    backup_running          BOOLEAN NOT NULL DEFAULT FALSE,
    pending_events          INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT ck_devices_pending_events_nonnegative
        CHECK (pending_events >= 0)
);

CREATE INDEX IF NOT EXISTS ix_devices_agency
    ON devices (agency_code);

CREATE INDEX IF NOT EXISTS ix_devices_last_seen
    ON devices (last_seen_at_utc);

CREATE TABLE IF NOT EXISTS inventory_snapshots
(
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id      UUID NOT NULL,
    device_id        UUID NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    captured_at_utc  TIMESTAMPTZ NOT NULL,
    payload          JSONB NOT NULL,
    UNIQUE (device_id, snapshot_id)
);

CREATE INDEX IF NOT EXISTS ix_inventory_device_captured
    ON inventory_snapshots (device_id, captured_at_utc DESC);

CREATE TABLE IF NOT EXISTS backup_runs
(
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id         UUID NOT NULL,
    device_id        UUID NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    started_at_utc   TIMESTAMPTZ NOT NULL,
    finished_at_utc  TIMESTAMPTZ NOT NULL,
    status           VARCHAR(32) NOT NULL,
    payload          JSONB NOT NULL,
    UNIQUE (device_id, event_id),
    CONSTRAINT ck_backup_runs_dates
        CHECK (finished_at_utc >= started_at_utc)
);

CREATE INDEX IF NOT EXISTS ix_backup_runs_device_finished
    ON backup_runs (device_id, finished_at_utc DESC);

CREATE TABLE IF NOT EXISTS audit_events
(
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at_utc  TIMESTAMPTZ NOT NULL,
    actor_type       VARCHAR(32) NOT NULL,
    actor_id         VARCHAR(255) NOT NULL,
    action           VARCHAR(128) NOT NULL,
    target_type      VARCHAR(64),
    target_id        VARCHAR(255),
    detail           JSONB
);

CREATE INDEX IF NOT EXISTS ix_audit_events_occurred
    ON audit_events (occurred_at_utc DESC);
