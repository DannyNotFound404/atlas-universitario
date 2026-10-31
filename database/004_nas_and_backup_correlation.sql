CREATE TABLE agencies
(
    code                    VARCHAR(64) PRIMARY KEY,
    display_name            VARCHAR(128) NOT NULL,
    location                VARCHAR(192),
    responsible             VARCHAR(128),
    default_backup_provider VARCHAR(16) NOT NULL DEFAULT 'ATLAS',
    enabled                 BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc          TIMESTAMPTZ NOT NULL,
    updated_at_utc          TIMESTAMPTZ NOT NULL,
    updated_by              VARCHAR(255) NOT NULL,
    CONSTRAINT ck_agencies_code
        CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,63}$'),
    CONSTRAINT ck_agencies_provider
        CHECK (default_backup_provider IN ('ATLAS', 'ACRONIS', 'EXTERNAL'))
);

INSERT INTO agencies
    (code, display_name, default_backup_provider, enabled,
     created_at_utc, updated_at_utc, updated_by)
SELECT DISTINCT
    source.agency_code,
    source.agency_code,
    'ATLAS',
    TRUE,
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP,
    'migration'
FROM
(
    SELECT agency_code FROM devices
    UNION
    SELECT agency_code FROM enrollment_tokens
) source
WHERE source.agency_code IS NOT NULL
ON CONFLICT (code) DO NOTHING;

ALTER TABLE enrollment_tokens
    ADD CONSTRAINT fk_enrollment_tokens_agency
    FOREIGN KEY (agency_code) REFERENCES agencies(code)
    ON UPDATE CASCADE ON DELETE RESTRICT;

ALTER TABLE devices
    ADD COLUMN backup_provider VARCHAR(16) NOT NULL DEFAULT 'ATLAS',
    ADD CONSTRAINT ck_devices_backup_provider
        CHECK (backup_provider IN ('ATLAS', 'ACRONIS', 'EXTERNAL')),
    ADD CONSTRAINT fk_devices_agency
        FOREIGN KEY (agency_code) REFERENCES agencies(code)
        ON UPDATE CASCADE ON DELETE RESTRICT;

CREATE TABLE nas_appliances
(
    id                  UUID PRIMARY KEY,
    code                VARCHAR(64) NOT NULL UNIQUE,
    agency_code         VARCHAR(64) NOT NULL REFERENCES agencies(code)
                            ON UPDATE CASCADE ON DELETE RESTRICT,
    display_name        VARCHAR(128) NOT NULL,
    host                VARCHAR(255) NOT NULL,
    smb_port            INTEGER NOT NULL DEFAULT 445,
    share_name          VARCHAR(128) NOT NULL,
    manufacturer        VARCHAR(96),
    model               VARCHAR(128),
    serial_number       VARCHAR(128),
    is_primary          BOOLEAN NOT NULL DEFAULT TRUE,
    enabled             BOOLEAN NOT NULL DEFAULT TRUE,
    last_probe_at_utc   TIMESTAMPTZ,
    last_probe_status   VARCHAR(16) NOT NULL DEFAULT 'UNKNOWN',
    last_probe_latency_ms INTEGER,
    total_bytes         BIGINT,
    free_bytes          BIGINT,
    last_probe_error    VARCHAR(256),
    created_at_utc      TIMESTAMPTZ NOT NULL,
    updated_at_utc      TIMESTAMPTZ NOT NULL,
    updated_by          VARCHAR(255) NOT NULL,
    CONSTRAINT ck_nas_code
        CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,63}$'),
    CONSTRAINT ck_nas_port CHECK (smb_port BETWEEN 1 AND 65535),
    CONSTRAINT ck_nas_probe_status
        CHECK (last_probe_status IN ('UNKNOWN', 'ONLINE', 'DEGRADED', 'OFFLINE')),
    CONSTRAINT ck_nas_latency
        CHECK (last_probe_latency_ms IS NULL OR last_probe_latency_ms >= 0),
    CONSTRAINT ck_nas_capacity
        CHECK
        (
            (total_bytes IS NULL AND free_bytes IS NULL)
            OR
            (total_bytes IS NOT NULL AND free_bytes IS NOT NULL
             AND total_bytes >= 0 AND free_bytes >= 0
             AND free_bytes <= total_bytes)
        )
);

CREATE UNIQUE INDEX ux_nas_primary_per_agency
    ON nas_appliances (agency_code)
    WHERE is_primary = TRUE AND enabled = TRUE;

CREATE INDEX ix_nas_agency
    ON nas_appliances (agency_code, enabled);

ALTER TABLE devices
    ADD COLUMN nas_appliance_id UUID REFERENCES nas_appliances(id)
        ON UPDATE CASCADE ON DELETE SET NULL;

CREATE INDEX ix_devices_backup_policy
    ON devices (backup_provider, nas_appliance_id)
    WHERE enabled = TRUE;

CREATE TABLE nas_probe_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nas_appliance_id    UUID NOT NULL REFERENCES nas_appliances(id)
                            ON DELETE CASCADE,
    probed_at_utc       TIMESTAMPTZ NOT NULL,
    status              VARCHAR(16) NOT NULL,
    latency_ms          INTEGER,
    total_bytes         BIGINT,
    free_bytes          BIGINT,
    error_code          VARCHAR(64),
    CONSTRAINT ck_nas_probe_history_status
        CHECK (status IN ('ONLINE', 'DEGRADED', 'OFFLINE')),
    CONSTRAINT ck_nas_probe_history_latency
        CHECK (latency_ms IS NULL OR latency_ms >= 0)
);

CREATE INDEX ix_nas_probe_history_appliance
    ON nas_probe_history (nas_appliance_id, probed_at_utc DESC);
