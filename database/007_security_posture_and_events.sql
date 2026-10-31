CREATE TABLE security_posture_snapshots
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id     UUID NOT NULL,
    device_id       UUID NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    captured_at_utc TIMESTAMPTZ NOT NULL,
    payload         JSONB NOT NULL,
    received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_security_posture_device_snapshot UNIQUE (device_id, snapshot_id)
);

CREATE INDEX ix_security_posture_device_captured
    ON security_posture_snapshots (device_id, captured_at_utc DESC);

CREATE TABLE endpoint_security_events
(
    id                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id               UUID NOT NULL,
    device_id              UUID NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    channel                VARCHAR(192) NOT NULL,
    record_id              BIGINT NOT NULL,
    event_code             INTEGER NOT NULL,
    occurred_at_utc        TIMESTAMPTZ NOT NULL,
    category               VARCHAR(64) NOT NULL,
    outcome                VARCHAR(32) NOT NULL,
    logon_type             INTEGER NULL,
    user_name              VARCHAR(256) NULL,
    user_domain            VARCHAR(256) NULL,
    source_address         VARCHAR(64) NULL,
    workstation_name       VARCHAR(256) NULL,
    payload                JSONB NOT NULL,
    received_at_utc        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_security_event_device_record UNIQUE (device_id, channel, record_id)
);

CREATE INDEX ix_security_events_occurred
    ON endpoint_security_events (occurred_at_utc DESC);

CREATE INDEX ix_security_events_device_occurred
    ON endpoint_security_events (device_id, occurred_at_utc DESC);

CREATE INDEX ix_security_events_category_outcome
    ON endpoint_security_events (category, outcome, occurred_at_utc DESC);
