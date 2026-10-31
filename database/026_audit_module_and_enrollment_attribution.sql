ALTER TABLE enrollment_tokens
    ADD COLUMN IF NOT EXISTS issuance_id UUID,
    ADD COLUMN IF NOT EXISTS issued_by VARCHAR(255),
    ADD COLUMN IF NOT EXISTS issued_by_role VARCHAR(32),
    ADD COLUMN IF NOT EXISTS issued_from_ip VARCHAR(64),
    ADD COLUMN IF NOT EXISTS issuance_source VARCHAR(32) NOT NULL DEFAULT 'LEGACY',
    ADD COLUMN IF NOT EXISTS installer_version VARCHAR(64);

CREATE UNIQUE INDEX IF NOT EXISTS ux_enrollment_tokens_issuance_id
    ON enrollment_tokens (issuance_id)
    WHERE issuance_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS device_enrollment_events
(
    id                          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issuance_id                 UUID UNIQUE,
    device_id                   UUID NOT NULL REFERENCES devices(id) ON DELETE RESTRICT,
    installation_id             UUID NOT NULL,
    agency_code                 VARCHAR(64) NOT NULL,
    machine_name                VARCHAR(255) NOT NULL,
    issued_by                   VARCHAR(255),
    issued_by_role              VARCHAR(32),
    issued_from_ip              VARCHAR(64),
    issuance_source             VARCHAR(32) NOT NULL,
    installer_version           VARCHAR(64),
    issued_at_utc               TIMESTAMPTZ,
    token_expires_at_utc        TIMESTAMPTZ,
    enrolled_at_utc             TIMESTAMPTZ NOT NULL,
    enrolled_from_ip            VARCHAR(64),
    agent_version               VARCHAR(64) NOT NULL,
    backup_client_version       VARCHAR(64),
    outcome                     VARCHAR(32) NOT NULL DEFAULT 'SUCCEEDED',
    attribution_status          VARCHAR(32) NOT NULL,
    is_reenrollment             BOOLEAN NOT NULL DEFAULT FALSE,
    execution_identity_verified BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT ck_device_enrollment_outcome
        CHECK (outcome IN ('SUCCEEDED')),
    CONSTRAINT ck_device_enrollment_attribution
        CHECK (attribution_status IN ('CONFIRMED', 'SYSTEM', 'UNAVAILABLE'))
);

CREATE INDEX IF NOT EXISTS ix_device_enrollment_events_enrolled
    ON device_enrollment_events (enrolled_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_device_enrollment_events_device
    ON device_enrollment_events (device_id, enrolled_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_device_enrollment_events_issuer
    ON device_enrollment_events (issued_by, enrolled_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_device_enrollment_events_agency
    ON device_enrollment_events (agency_code, enrolled_at_utc DESC);

INSERT INTO device_enrollment_events
    (issuance_id, device_id, installation_id, agency_code, machine_name,
     issued_by, issued_by_role, issued_from_ip, issuance_source,
     installer_version, issued_at_utc, token_expires_at_utc,
     enrolled_at_utc, enrolled_from_ip, agent_version,
     backup_client_version, outcome, attribution_status,
     is_reenrollment, execution_identity_verified)
SELECT
    NULL, d.id, d.installation_id, d.agency_code, d.machine_name,
    NULL, NULL, NULL, 'HISTORICAL', NULL, NULL, NULL,
    d.enrolled_at_utc, NULL, d.agent_version,
    d.backup_client_version, 'SUCCEEDED', 'UNAVAILABLE',
    FALSE, FALSE
FROM devices d
WHERE NOT EXISTS
(
    SELECT 1
    FROM device_enrollment_events e
    WHERE e.device_id = d.id
      AND e.issuance_source = 'HISTORICAL'
);

CREATE INDEX IF NOT EXISTS ix_audit_events_actor
    ON audit_events (actor_id, occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_action
    ON audit_events (action, occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_target
    ON audit_events (target_type, target_id, occurred_at_utc DESC);
