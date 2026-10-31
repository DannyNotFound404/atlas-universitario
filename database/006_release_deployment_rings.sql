ALTER TABLE devices
    ADD COLUMN update_ring_code VARCHAR(16) NOT NULL DEFAULT 'GENERAL';

ALTER TABLE devices
    ADD CONSTRAINT ck_devices_update_ring
    CHECK (update_ring_code IN ('GENERAL', 'PILOT'));

CREATE INDEX ix_devices_update_ring
    ON devices (update_ring_code)
    WHERE enabled = TRUE;

CREATE TABLE release_deployments
(
    scope_type      VARCHAR(16) NOT NULL,
    scope_value     VARCHAR(64) NOT NULL,
    release_id      UUID NOT NULL,
    paused          BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at_utc  TIMESTAMPTZ NOT NULL,
    updated_by      VARCHAR(128) NOT NULL,
    PRIMARY KEY (scope_type, scope_value),
    CONSTRAINT ck_release_deployments_scope_type
        CHECK (scope_type IN ('DEVICE', 'RING', 'AGENCY')),
    CONSTRAINT ck_release_deployments_scope_value
        CHECK (length(btrim(scope_value)) > 0)
);

CREATE INDEX ix_release_deployments_release
    ON release_deployments (release_id);

CREATE INDEX ix_release_deployments_active
    ON release_deployments (scope_type, scope_value)
    WHERE paused = FALSE;
