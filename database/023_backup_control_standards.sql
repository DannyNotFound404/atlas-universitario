CREATE TABLE backup_control_standards
(
    id                         UUID PRIMARY KEY,
    code                       VARCHAR(64) NOT NULL,
    version                    VARCHAR(24) NOT NULL,
    name                       VARCHAR(160) NOT NULL,
    description                VARCHAR(1000) NOT NULL,
    scope_type                 VARCHAR(24),
    provider                   VARCHAR(24),
    operation_mode             VARCHAR(24),
    technology                 VARCHAR(160),
    task_name                  VARCHAR(160),
    protected_scope            TEXT,
    protected_scope_categories TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    frequency_hours            INTEGER,
    grace_minutes              INTEGER,
    retention_days             INTEGER,
    rpo_hours                  INTEGER,
    rto_minutes                INTEGER,
    restore_test_interval_days INTEGER,
    technical_responsible      VARCHAR(160) NOT NULL
                                   DEFAULT 'Coordinador de Ciberseguridad',
    active                     BOOLEAN NOT NULL DEFAULT TRUE,
    is_system                  BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc             TIMESTAMPTZ NOT NULL,
    created_by                 VARCHAR(64) NOT NULL,
    CONSTRAINT ux_backup_control_standard_version UNIQUE (code, version),
    CONSTRAINT ck_backup_control_standard_code
        CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,63}$'),
    CONSTRAINT ck_backup_control_standard_scope
        CHECK (scope_type IS NULL OR
               scope_type IN ('ENDPOINT', 'ON_PREM_SYSTEM', 'LOCAL_EXPORT')),
    CONSTRAINT ck_backup_control_standard_provider
        CHECK (provider IS NULL OR
               provider IN ('ATLAS_BACKUPNAS', 'ACRONIS', 'OTHER')),
    CONSTRAINT ck_backup_control_standard_mode
        CHECK (operation_mode IS NULL OR
               operation_mode IN ('AUTOMATIC', 'SEMI_AUTOMATIC', 'MANUAL')),
    CONSTRAINT ck_backup_control_standard_schedule
        CHECK ((frequency_hours IS NULL OR frequency_hours BETWEEN 1 AND 8760) AND
               (grace_minutes IS NULL OR grace_minutes BETWEEN 0 AND 10080)),
    CONSTRAINT ck_backup_control_standard_retention
        CHECK (retention_days IS NULL OR retention_days BETWEEN 1 AND 3650),
    CONSTRAINT ck_backup_control_standard_rpo
        CHECK (rpo_hours IS NULL OR rpo_hours BETWEEN 1 AND 8760),
    CONSTRAINT ck_backup_control_standard_rto
        CHECK (rto_minutes IS NULL OR rto_minutes BETWEEN 1 AND 10080),
    CONSTRAINT ck_backup_control_standard_restore_interval
        CHECK (restore_test_interval_days IS NULL OR
               restore_test_interval_days BETWEEN 1 AND 730),
    CONSTRAINT ck_backup_control_standard_responsible
        CHECK (technical_responsible = 'Coordinador de Ciberseguridad')
);

CREATE UNIQUE INDEX ux_backup_control_standard_active
    ON backup_control_standards (code)
    WHERE active = TRUE;

INSERT INTO backup_control_standards
    (id, code, version, name, description, scope_type, provider,
     operation_mode, technology, task_name, protected_scope,
     protected_scope_categories, frequency_hours, grace_minutes,
     retention_days, rpo_hours, rto_minutes, restore_test_interval_days,
     active, is_system, created_at_utc, created_by)
VALUES
    ('00000000-0000-4000-8000-000000000101',
     'BKP-BASE-INSTITUCIONAL', '1.0', 'Base institucional',
     'Marco mínimo para controles personalizados. Conserva la estructura, la retención institucional y la trazabilidad sin imponer una tecnología.',
     NULL, NULL, NULL, NULL, NULL, NULL, ARRAY[]::TEXT[],
     NULL, 120, 180, NULL, NULL, 90,
     TRUE, TRUE, CURRENT_TIMESTAMP, 'System'),
    ('00000000-0000-4000-8000-000000000102',
     'BKP-ENDPOINT-ATLAS', '1.0', 'Endpoint con ATLAS Backup / NAS',
     'Protección diaria esperada para endpoints que utilizan ATLAS Backup y un destino NAS autorizado.',
     'ENDPOINT', 'ATLAS_BACKUPNAS', 'SEMI_AUTOMATIC', 'Atlas Backup',
     'Respaldo diario',
     'Carpetas estándar de trabajo configuradas para el usuario asignado al equipo.',
     ARRAY['STANDARD_BACKUPNAS'], 24, 120, 180, 24, NULL, 90,
     TRUE, TRUE, CURRENT_TIMESTAMP, 'System'),
    ('00000000-0000-4000-8000-000000000103',
     'BKP-ENDPOINT-ACRONIS', '1.0', 'Endpoint con Acronis Cloud',
     'Protección diaria esperada para endpoints incluidos en una política autorizada de Acronis Cloud.',
     'ENDPOINT', 'ACRONIS', 'AUTOMATIC', 'Acronis Cloud',
     'Protección diaria',
     'Información y software incluidos en la política autorizada de Acronis.',
     ARRAY['ACRONIS_POLICY'], 24, 120, 180, 24, NULL, 90,
     TRUE, TRUE, CURRENT_TIMESTAMP, 'System');

ALTER TABLE backup_control_profiles
    ADD COLUMN standard_id UUID,
    ADD COLUMN configuration_mode VARCHAR(24) NOT NULL DEFAULT 'MANUAL',
    ADD COLUMN configuration_reason VARCHAR(1000),
    ADD COLUMN protected_scope_categories TEXT[] NOT NULL
        DEFAULT ARRAY[]::TEXT[],
    ADD COLUMN protected_scope_additional TEXT,
    ADD COLUMN protected_scope_exclusions TEXT,
    ADD COLUMN field_origins JSONB NOT NULL DEFAULT '{}'::JSONB,
    ADD COLUMN exception_type VARCHAR(24) NOT NULL DEFAULT 'NONE',
    ADD COLUMN exception_reason VARCHAR(2000),
    ADD COLUMN exception_expires_at_utc TIMESTAMPTZ;

UPDATE backup_control_profiles
SET standard_id = '00000000-0000-4000-8000-000000000101',
    configuration_mode = 'MANUAL',
    configuration_reason =
        'Perfil existente incorporado a la base institucional.',
    field_origins = '{"profile":"MANUAL"}'::JSONB;

ALTER TABLE backup_control_profiles
    ALTER COLUMN standard_id SET NOT NULL,
    ADD CONSTRAINT fk_backup_control_profile_standard
        FOREIGN KEY (standard_id) REFERENCES backup_control_standards(id)
            ON UPDATE CASCADE ON DELETE RESTRICT,
    ADD CONSTRAINT ck_backup_control_profile_configuration_mode
        CHECK (configuration_mode IN ('STANDARD', 'CUSTOMIZED', 'MANUAL')),
    ADD CONSTRAINT ck_backup_control_profile_configuration_reason
        CHECK (configuration_mode = 'STANDARD' OR
               NULLIF(BTRIM(configuration_reason), '') IS NOT NULL),
    ADD CONSTRAINT ck_backup_control_profile_scope_categories
        CHECK (protected_scope_categories <@ ARRAY[
            'STANDARD_BACKUPNAS', 'ACRONIS_POLICY', 'DOCUMENTS', 'DESKTOP',
            'IMAGES', 'FAVORITES', 'DOWNLOADS', 'OTHER']::TEXT[]),
    ADD CONSTRAINT ck_backup_control_profile_field_origins
        CHECK (jsonb_typeof(field_origins) = 'object'),
    ADD CONSTRAINT ck_backup_control_profile_exception_type
        CHECK (exception_type IN ('NONE', 'OPERATIONAL', 'NORMATIVE')),
    ADD CONSTRAINT ck_backup_control_profile_exception_detail
        CHECK ((exception_type = 'NONE' AND exception_reason IS NULL AND
                exception_expires_at_utc IS NULL) OR
               (exception_type = 'OPERATIONAL' AND
                NULLIF(BTRIM(exception_reason), '') IS NOT NULL AND
                exception_expires_at_utc IS NOT NULL) OR
               (exception_type = 'NORMATIVE' AND
                NULLIF(BTRIM(exception_reason), '') IS NOT NULL));

CREATE INDEX ix_backup_control_profiles_standard
    ON backup_control_profiles (standard_id, configuration_mode, enabled);

CREATE INDEX ix_backup_control_profiles_exception
    ON backup_control_profiles (exception_type, exception_expires_at_utc)
    WHERE exception_type <> 'NONE';
