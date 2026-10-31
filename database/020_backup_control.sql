CREATE TABLE backup_control_profiles
(
    id                         UUID PRIMARY KEY,
    code                       VARCHAR(64) NOT NULL UNIQUE,
    scope_type                 VARCHAR(24) NOT NULL,
    device_id                  UUID REFERENCES devices(id) ON DELETE SET NULL,
    agency_code                VARCHAR(64) NOT NULL REFERENCES agencies(code)
                                   ON UPDATE CASCADE ON DELETE RESTRICT,
    system_name                VARCHAR(160) NOT NULL,
    protected_scope            TEXT NOT NULL,
    provider                   VARCHAR(24) NOT NULL,
    operation_mode             VARCHAR(24) NOT NULL,
    technology                 VARCHAR(160) NOT NULL,
    task_name                  VARCHAR(160),
    nas_appliance_id           UUID REFERENCES nas_appliances(id)
                                   ON UPDATE CASCADE ON DELETE SET NULL,
    expected_destination       VARCHAR(512),
    frequency_hours            INTEGER NOT NULL DEFAULT 24,
    grace_minutes              INTEGER NOT NULL DEFAULT 120,
    retention_days             INTEGER,
    rpo_hours                  INTEGER,
    rto_minutes                INTEGER,
    restore_test_interval_days INTEGER,
    technical_responsible      VARCHAR(160) NOT NULL,
    process_owner              VARCHAR(160),
    approval_status            VARCHAR(24) NOT NULL DEFAULT 'DRAFT',
    approved_by                VARCHAR(160),
    approved_at_utc            TIMESTAMPTZ,
    approval_comment           VARCHAR(1000),
    enabled                    BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc             TIMESTAMPTZ NOT NULL,
    created_by                 VARCHAR(64) NOT NULL,
    updated_at_utc             TIMESTAMPTZ NOT NULL,
    updated_by                 VARCHAR(64) NOT NULL,
    CONSTRAINT ck_backup_control_profile_code
        CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,63}$'),
    CONSTRAINT ck_backup_control_profile_scope
        CHECK (scope_type IN ('ENDPOINT', 'ON_PREM_SYSTEM', 'LOCAL_EXPORT')),
    CONSTRAINT ck_backup_control_profile_provider
        CHECK (provider IN ('ATLAS_BACKUPNAS', 'ACRONIS', 'OTHER')),
    CONSTRAINT ck_backup_control_profile_mode
        CHECK (operation_mode IN ('AUTOMATIC', 'SEMI_AUTOMATIC', 'MANUAL')),
    CONSTRAINT ck_backup_control_profile_responsible
        CHECK (technical_responsible = 'Coordinador de Ciberseguridad'),
    CONSTRAINT ck_backup_control_profile_schedule
        CHECK (frequency_hours BETWEEN 1 AND 8760 AND
               grace_minutes BETWEEN 0 AND 10080),
    CONSTRAINT ck_backup_control_profile_targets
        CHECK ((provider <> 'ATLAS_BACKUPNAS') OR device_id IS NOT NULL),
    CONSTRAINT ck_backup_control_profile_retention
        CHECK (retention_days IS NULL OR retention_days BETWEEN 1 AND 3650),
    CONSTRAINT ck_backup_control_profile_rpo
        CHECK (rpo_hours IS NULL OR rpo_hours BETWEEN 1 AND 8760),
    CONSTRAINT ck_backup_control_profile_rto
        CHECK (rto_minutes IS NULL OR rto_minutes BETWEEN 1 AND 10080),
    CONSTRAINT ck_backup_control_profile_restore_interval
        CHECK (restore_test_interval_days IS NULL OR
               restore_test_interval_days BETWEEN 1 AND 730),
    CONSTRAINT ck_backup_control_profile_approval
        CHECK (approval_status IN
            ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'RETURNED')),
    CONSTRAINT ck_backup_control_profile_approved_actor
        CHECK ((approval_status = 'APPROVED') =
               (approved_by IS NOT NULL AND approved_at_utc IS NOT NULL))
);

CREATE INDEX ix_backup_control_profiles_agency
    ON backup_control_profiles (agency_code, enabled, approval_status);

CREATE INDEX ix_backup_control_profiles_device
    ON backup_control_profiles (device_id)
    WHERE device_id IS NOT NULL;

CREATE TABLE backup_control_observations
(
    id                  UUID PRIMARY KEY,
    profile_id          UUID NOT NULL REFERENCES backup_control_profiles(id)
                            ON DELETE CASCADE,
    backup_run_id       BIGINT REFERENCES backup_runs(id) ON DELETE SET NULL,
    source              VARCHAR(32) NOT NULL,
    result              VARCHAR(16) NOT NULL,
    period_start_utc    TIMESTAMPTZ NOT NULL,
    period_end_utc      TIMESTAMPTZ NOT NULL,
    started_at_utc      TIMESTAMPTZ,
    finished_at_utc     TIMESTAMPTZ,
    external_reference  VARCHAR(255),
    detail              VARCHAR(2000),
    created_at_utc      TIMESTAMPTZ NOT NULL,
    created_by          VARCHAR(64) NOT NULL,
    CONSTRAINT ck_backup_control_observation_source
        CHECK (source IN ('AGENT', 'ACRONIS_EVIDENCE', 'MANUAL')),
    CONSTRAINT ck_backup_control_observation_result
        CHECK (result IN ('SUCCESS', 'WARNING', 'FAILED')),
    CONSTRAINT ck_backup_control_observation_period
        CHECK (period_end_utc >= period_start_utc),
    CONSTRAINT ck_backup_control_observation_execution
        CHECK (finished_at_utc IS NULL OR started_at_utc IS NULL OR
               finished_at_utc >= started_at_utc)
);

CREATE INDEX ix_backup_control_observations_profile
    ON backup_control_observations (profile_id, period_end_utc DESC);

CREATE UNIQUE INDEX ux_backup_control_observation_run
    ON backup_control_observations (profile_id, backup_run_id)
    WHERE backup_run_id IS NOT NULL;

CREATE UNIQUE INDEX ux_backup_control_observation_reference
    ON backup_control_observations (profile_id, source, external_reference)
    WHERE external_reference IS NOT NULL;

CREATE TABLE backup_restoration_tests
(
    id                      UUID PRIMARY KEY,
    profile_id              UUID NOT NULL REFERENCES backup_control_profiles(id)
                                ON DELETE CASCADE,
    operational_case_id     UUID REFERENCES operational_cases(id)
                                ON DELETE SET NULL,
    modality                VARCHAR(24) NOT NULL,
    result                  VARCHAR(16) NOT NULL,
    backup_used_at_utc      TIMESTAMPTZ,
    started_at_utc          TIMESTAMPTZ NOT NULL,
    finished_at_utc         TIMESTAMPTZ NOT NULL,
    tested_scope            TEXT NOT NULL,
    integrity_verified      BOOLEAN NOT NULL,
    functionality_verified  BOOLEAN NOT NULL,
    rto_achieved_minutes    INTEGER,
    executed_by             VARCHAR(160) NOT NULL,
    validated_by            VARCHAR(160),
    validated_at_utc        TIMESTAMPTZ,
    notes                   VARCHAR(4000),
    historical              BOOLEAN NOT NULL DEFAULT FALSE,
    next_test_due_utc       TIMESTAMPTZ,
    created_at_utc          TIMESTAMPTZ NOT NULL,
    created_by              VARCHAR(64) NOT NULL,
    CONSTRAINT ck_backup_restoration_modality
        CHECK (modality IN ('FILE', 'APPLICATION', 'SYSTEM', 'HOT_RECOVERY')),
    CONSTRAINT ck_backup_restoration_result
        CHECK (result IN ('SUCCESS', 'WARNING', 'FAILED')),
    CONSTRAINT ck_backup_restoration_dates
        CHECK (finished_at_utc >= started_at_utc),
    CONSTRAINT ck_backup_restoration_rto
        CHECK (rto_achieved_minutes IS NULL OR
               rto_achieved_minutes BETWEEN 0 AND 10080),
    CONSTRAINT ck_backup_restoration_validation
        CHECK ((validated_at_utc IS NULL) OR validated_by IS NOT NULL),
    CONSTRAINT ck_backup_restoration_success_verified
        CHECK (result <> 'SUCCESS' OR
               (integrity_verified AND functionality_verified))
);

CREATE INDEX ix_backup_restoration_tests_profile
    ON backup_restoration_tests (profile_id, finished_at_utc DESC);

CREATE INDEX ix_backup_restoration_tests_due
    ON backup_restoration_tests (next_test_due_utc)
    WHERE next_test_due_utc IS NOT NULL;

CREATE TABLE backup_control_evidence
(
    id                    UUID PRIMARY KEY,
    profile_id            UUID NOT NULL REFERENCES backup_control_profiles(id)
                              ON DELETE CASCADE,
    observation_id        UUID REFERENCES backup_control_observations(id)
                              ON DELETE SET NULL,
    restoration_test_id   UUID REFERENCES backup_restoration_tests(id)
                              ON DELETE SET NULL,
    operational_case_id   UUID REFERENCES operational_cases(id)
                              ON DELETE SET NULL,
    evidence_type         VARCHAR(24) NOT NULL,
    original_file_name    VARCHAR(255) NOT NULL,
    media_type            VARCHAR(120) NOT NULL,
    stored_relative_path  VARCHAR(500) NOT NULL UNIQUE,
    size_bytes            BIGINT NOT NULL,
    sha256                CHAR(64) NOT NULL,
    evidence_at_utc       TIMESTAMPTZ NOT NULL,
    description           VARCHAR(1000),
    source                VARCHAR(160),
    status                VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    created_at_utc        TIMESTAMPTZ NOT NULL,
    created_by            VARCHAR(64) NOT NULL,
    CONSTRAINT ck_backup_control_evidence_type
        CHECK (evidence_type IN
            ('REPORT', 'LOG', 'SCREENSHOT', 'ACT', 'EXPORT', 'OTHER')),
    CONSTRAINT ck_backup_control_evidence_size
        CHECK (size_bytes BETWEEN 1 AND 25000000),
    CONSTRAINT ck_backup_control_evidence_hash
        CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_backup_control_evidence_status
        CHECK (status IN ('ACTIVE', 'SUPERSEDED', 'WITHDRAWN'))
);

CREATE INDEX ix_backup_control_evidence_profile
    ON backup_control_evidence (profile_id, evidence_at_utc DESC);

CREATE INDEX ix_backup_control_evidence_observation
    ON backup_control_evidence (observation_id)
    WHERE observation_id IS NOT NULL;

CREATE INDEX ix_backup_control_evidence_restoration
    ON backup_control_evidence (restoration_test_id)
    WHERE restoration_test_id IS NOT NULL;
