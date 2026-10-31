-- Additive migration: existing profiles, scope descriptions and history remain intact.
CREATE TABLE backup_control_plans (
    id uuid PRIMARY KEY,
    name varchar(160) NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    configuration jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    updated_by varchar(160) NOT NULL
);
CREATE TABLE backup_control_plan_versions (
    plan_id uuid NOT NULL REFERENCES backup_control_plans(id),
    revision integer NOT NULL CHECK (revision > 0),
    configuration jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    created_by varchar(160) NOT NULL,
    PRIMARY KEY (plan_id, revision)
);
ALTER TABLE backup_control_profiles
    ADD COLUMN plan_id uuid,
    ADD COLUMN plan_revision integer,
    ADD COLUMN protected_folders text[] NOT NULL DEFAULT '{}',
    ADD COLUMN scope_override boolean NOT NULL DEFAULT false,
    ADD CONSTRAINT backup_profile_plan_pair CHECK ((plan_id IS NULL) = (plan_revision IS NULL)),
    ADD CONSTRAINT backup_profile_plan_version FOREIGN KEY (plan_id, plan_revision)
        REFERENCES backup_control_plan_versions(plan_id, revision);
CREATE INDEX ix_backup_profiles_plan ON backup_control_profiles(plan_id);
-- One request key covers the result, optional evidence and audit in one transaction.
CREATE TABLE backup_control_record_requests (
    id uuid PRIMARY KEY,
    profile_id uuid NOT NULL REFERENCES backup_control_profiles(id),
    fingerprint char(64) NOT NULL,
    created_at_utc timestamptz NOT NULL
);
