CREATE TABLE atlas_settings (
    section varchar(32) PRIMARY KEY,
    revision integer NOT NULL CHECK (revision > 0),
    value jsonb NOT NULL,
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by varchar(64) NOT NULL
);
CREATE TABLE atlas_setting_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    section varchar(32) NOT NULL,
    revision integer NOT NULL,
    before_value jsonb,
    after_value jsonb NOT NULL,
    reason varchar(500) NOT NULL,
    actor varchar(64) NOT NULL,
    occurred_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(section,revision)
);
CREATE FUNCTION atlas_evidence_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Issued evidence is immutable' USING ERRCODE='23514'; END;
$$;
CREATE TRIGGER atlas_setting_history_immutable BEFORE UPDATE OR DELETE ON atlas_setting_history
FOR EACH ROW EXECUTE FUNCTION atlas_evidence_immutable();

CREATE TABLE atlas_report_variants (
    id uuid PRIMARY KEY,
    owner_id uuid NOT NULL REFERENCES admin_users(id),
    name varchar(120) NOT NULL,
    request jsonb NOT NULL,
    revision integer NOT NULL DEFAULT 1,
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE atlas_report_schedules (
    id uuid PRIMARY KEY,
    owner_id uuid NOT NULL REFERENCES admin_users(id),
    name varchar(120) NOT NULL,
    request jsonb NOT NULL,
    cadence varchar(16) NOT NULL CHECK(cadence IN ('DAILY','WEEKLY','MONTHLY','QUARTERLY')),
    time_zone varchar(100) NOT NULL,
    next_at_utc timestamptz NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    revision integer NOT NULL DEFAULT 1,
    suspension_reason varchar(120),
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE atlas_report_jobs (
    id uuid PRIMARY KEY,
    owner_id uuid NOT NULL REFERENCES admin_users(id),
    request_key uuid NOT NULL,
    request jsonb NOT NULL,
    schedule_id uuid REFERENCES atlas_report_schedules(id),
    scheduled_at_utc timestamptz,
    status varchar(16) NOT NULL DEFAULT 'QUEUED' CHECK(status IN ('QUEUED','RUNNING','COMPLETE','FAILED','CANCELLED')),
    progress integer NOT NULL DEFAULT 0 CHECK(progress BETWEEN 0 AND 100),
    attempts integer NOT NULL DEFAULT 0,
    lease_id uuid,
    lease_until_utc timestamptz,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at_utc timestamptz,
    error_code varchar(64),
    UNIQUE(owner_id,request_key),
    UNIQUE(schedule_id,scheduled_at_utc)
);
CREATE INDEX atlas_report_jobs_queue ON atlas_report_jobs(status,created_at_utc);
CREATE TABLE atlas_report_snapshots (
    report_id uuid PRIMARY KEY REFERENCES atlas_report_jobs(id),
    snapshot jsonb NOT NULL,
    sha256 char(64) NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE atlas_report_files (
    report_id uuid NOT NULL REFERENCES atlas_report_snapshots(report_id),
    format varchar(8) NOT NULL CHECK(format IN ('csv','xlsx','pdf')),
    content bytea NOT NULL CHECK(octet_length(content) <= 25000000),
    sha256 char(64) NOT NULL,
    PRIMARY KEY(report_id,format)
);
CREATE TRIGGER atlas_report_snapshots_immutable BEFORE UPDATE OR DELETE ON atlas_report_snapshots
FOR EACH ROW EXECUTE FUNCTION atlas_evidence_immutable();
CREATE TRIGGER atlas_report_files_immutable BEFORE UPDATE OR DELETE ON atlas_report_files
FOR EACH ROW EXECUTE FUNCTION atlas_evidence_immutable();

ALTER TABLE admin_users ADD COLUMN security_revision integer NOT NULL DEFAULT 1;
CREATE TABLE atlas_admin_sessions (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES admin_users(id),
    security_revision integer NOT NULL,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    confirmed_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    revoked_at_utc timestamptz
);
CREATE INDEX atlas_admin_sessions_user ON atlas_admin_sessions(user_id,expires_at_utc);
