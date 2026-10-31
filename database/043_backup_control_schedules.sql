-- V4 pp.9-10 / reference7.2: operational scheduling is explicit and opt-in.
-- No agency attendance, holidays, old obligations or backup results are invented.
CREATE TABLE backup_control_schedules (
    scope_type varchar(12) NOT NULL CHECK(scope_type IN ('AGENCY','PLAN','PROFILE')),
    scope_key varchar(160) NOT NULL,
    revision integer NOT NULL CHECK(revision > 0),
    value jsonb NOT NULL CHECK(jsonb_typeof(value)='object'),
    updated_at_utc timestamptz NOT NULL,
    updated_by varchar(160) NOT NULL,
    PRIMARY KEY(scope_type,scope_key)
);
CREATE TABLE backup_control_schedule_history (
    scope_type varchar(12) NOT NULL,
    scope_key varchar(160) NOT NULL,
    revision integer NOT NULL CHECK(revision > 0),
    before_value jsonb,
    after_value jsonb NOT NULL,
    reason varchar(500) NOT NULL,
    actor varchar(160) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    PRIMARY KEY(scope_type,scope_key,revision),
    FOREIGN KEY(scope_type,scope_key) REFERENCES backup_control_schedules(scope_type,scope_key)
);
CREATE TRIGGER backup_control_schedule_history_immutable
BEFORE UPDATE OR DELETE ON backup_control_schedule_history
FOR EACH ROW EXECUTE FUNCTION atlas_preserve_audit_record();
