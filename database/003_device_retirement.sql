ALTER TABLE devices
    ADD COLUMN retired_at_utc TIMESTAMPTZ,
    ADD COLUMN retired_by VARCHAR(255),
    ADD COLUMN retirement_reason VARCHAR(256);

UPDATE devices
SET
    retired_at_utc = COALESCE(retired_at_utc, CURRENT_TIMESTAMP),
    retired_by = COALESCE(retired_by, 'migration'),
    retirement_reason = COALESCE(
        retirement_reason,
        'Dispositivo deshabilitado antes del registro formal de retiradas.')
WHERE enabled = FALSE;

ALTER TABLE devices
    ADD CONSTRAINT ck_devices_retirement_state
    CHECK
    (
        (
            enabled = TRUE
            AND retired_at_utc IS NULL
            AND retired_by IS NULL
            AND retirement_reason IS NULL
        )
        OR
        (
            enabled = FALSE
            AND retired_at_utc IS NOT NULL
            AND retired_by IS NOT NULL
            AND retirement_reason IS NOT NULL
        )
    );

CREATE INDEX IF NOT EXISTS ix_devices_enabled
    ON devices (enabled);

CREATE INDEX IF NOT EXISTS ix_devices_retired_at
    ON devices (retired_at_utc DESC)
    WHERE retired_at_utc IS NOT NULL;
