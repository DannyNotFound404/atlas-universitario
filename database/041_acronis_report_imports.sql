-- V4 pp. 6/16 (inventory), 9-10 (backup evidence), 21-22 (monitoring).
-- A consolidated report is evidence of its stated dates, not a restoration test.
-- Existing providers, plans, backup content and credentials remain outside this import.
CREATE TABLE acronis_report_imports (
    id uuid PRIMARY KEY,
    original_file_name varchar(255) NOT NULL,
    stored_relative_path varchar(500) NOT NULL,
    size_bytes bigint NOT NULL CHECK (size_bytes > 0),
    sha256 char(64) NOT NULL UNIQUE,
    report_at_utc timestamptz NOT NULL,
    date_offset_minutes integer NOT NULL CHECK (date_offset_minutes BETWEEN -720 AND 840),
    document jsonb NOT NULL,
    comparison jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    created_by varchar(160) NOT NULL,
    reviewed_at_utc timestamptz,
    reviewed_by varchar(160),
    review_fingerprint char(64),
    review_notes varchar(4000),
    CHECK ((reviewed_at_utc IS NULL) = (reviewed_by IS NULL)),
    CHECK ((reviewed_at_utc IS NULL) = (review_fingerprint IS NULL))
);
CREATE INDEX ix_acronis_imports_date ON acronis_report_imports(created_at_utc DESC,id);
CREATE TABLE acronis_report_observations (
    import_id uuid NOT NULL REFERENCES acronis_report_imports(id),
    source_row integer NOT NULL CHECK (source_row > 0),
    profile_id uuid NOT NULL REFERENCES backup_control_profiles(id),
    observation_id uuid NOT NULL REFERENCES backup_control_observations(id),
    reused bool NOT NULL,
    PRIMARY KEY(import_id,source_row),
    UNIQUE(import_id,profile_id)
);
