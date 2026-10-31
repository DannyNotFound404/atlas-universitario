CREATE TABLE atlas_server_releases
(
    release_version              VARCHAR(64) PRIMARY KEY,
    released_at_utc              TIMESTAMPTZ NOT NULL,
    previous_release_version     VARCHAR(64),
    summary                      VARCHAR(500) NOT NULL,
    features                     JSONB NOT NULL,
    changes_from_previous        JSONB NOT NULL,
    application_restore_outcome  VARCHAR(1200) NOT NULL,
    full_restore_outcome         VARCHAR(1200) NOT NULL,
    is_active                    BOOLEAN NOT NULL DEFAULT FALSE,
    observed_on_server           BOOLEAN NOT NULL DEFAULT FALSE,
    first_activated_at_utc       TIMESTAMPTZ,
    last_activated_at_utc        TIMESTAMPTZ,
    application_sha256           CHAR(64),
    schema_migration_count       INTEGER,
    evidence_source              VARCHAR(32) NOT NULL DEFAULT 'DOCUMENTATION',
    catalogued_at_utc            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_atlas_server_releases_features
        CHECK (jsonb_typeof(features) = 'array'),
    CONSTRAINT ck_atlas_server_releases_changes
        CHECK (jsonb_typeof(changes_from_previous) = 'array'),
    CONSTRAINT ck_atlas_server_releases_sha256
        CHECK (application_sha256 IS NULL OR application_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_atlas_server_releases_schema_count
        CHECK (schema_migration_count IS NULL OR schema_migration_count >= 0),
    CONSTRAINT ck_atlas_server_releases_evidence_source
        CHECK (evidence_source IN ('DOCUMENTATION', 'RUNTIME_AND_DOCUMENTATION'))
);

CREATE UNIQUE INDEX ux_atlas_server_releases_active
    ON atlas_server_releases (is_active)
    WHERE is_active;

CREATE INDEX ix_atlas_server_releases_released_at
    ON atlas_server_releases (released_at_utc DESC);
