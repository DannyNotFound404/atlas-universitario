CREATE SEQUENCE atlas_case_number_seq START WITH 1 INCREMENT BY 1;

CREATE TABLE operational_cases
(
    id                    UUID PRIMARY KEY,
    case_number           VARCHAR(32) NOT NULL UNIQUE,
    case_type             VARCHAR(32) NOT NULL,
    title                 VARCHAR(180) NOT NULL,
    description           TEXT NOT NULL,
    source                VARCHAR(120),
    severity              VARCHAR(16) NOT NULL,
    priority              VARCHAR(16) NOT NULL,
    status                VARCHAR(32) NOT NULL,
    agency_code           VARCHAR(64) REFERENCES agencies(code),
    assigned_to           VARCHAR(160),
    institutional_owner   VARCHAR(160),
    due_at_utc            TIMESTAMPTZ,
    closed_at_utc         TIMESTAMPTZ,
    created_at_utc        TIMESTAMPTZ NOT NULL,
    created_by            VARCHAR(64) NOT NULL,
    updated_at_utc        TIMESTAMPTZ NOT NULL,
    updated_by            VARCHAR(64) NOT NULL,
    CONSTRAINT ck_operational_cases_type CHECK
        (case_type IN ('INCIDENT', 'VULNERABILITY', 'EXCEPTION',
                       'RESTORATION', 'ACCESS', 'SUPPLIER', 'OTHER')),
    CONSTRAINT ck_operational_cases_severity CHECK
        (severity IN ('INFO', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL')),
    CONSTRAINT ck_operational_cases_priority CHECK
        (priority IN ('LOW', 'MEDIUM', 'HIGH', 'URGENT')),
    CONSTRAINT ck_operational_cases_status CHECK
        (status IN ('NEW', 'EVALUATING', 'ASSIGNED', 'IN_TREATMENT',
                    'PENDING_VALIDATION', 'CLOSED', 'ACCEPTED_EXCEPTION')),
    CONSTRAINT ck_operational_cases_dates CHECK
        (closed_at_utc IS NULL OR closed_at_utc >= created_at_utc)
);

CREATE INDEX ix_operational_cases_updated
    ON operational_cases (updated_at_utc DESC);

CREATE INDEX ix_operational_cases_status
    ON operational_cases (status, priority, updated_at_utc DESC);

CREATE INDEX ix_operational_cases_agency
    ON operational_cases (agency_code, updated_at_utc DESC);

CREATE TABLE operational_case_devices
(
    case_id   UUID NOT NULL REFERENCES operational_cases(id) ON DELETE CASCADE,
    device_id UUID NOT NULL REFERENCES devices(id) ON DELETE RESTRICT,
    PRIMARY KEY (case_id, device_id)
);

CREATE INDEX ix_operational_case_devices_device
    ON operational_case_devices (device_id, case_id);

CREATE TABLE operational_case_timeline
(
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    case_id          UUID NOT NULL REFERENCES operational_cases(id) ON DELETE CASCADE,
    event_type       VARCHAR(32) NOT NULL,
    comment          TEXT,
    from_status      VARCHAR(32),
    to_status        VARCHAR(32),
    actor_id         VARCHAR(64) NOT NULL,
    actor_role       VARCHAR(32) NOT NULL,
    occurred_at_utc  TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_operational_case_timeline_event CHECK
        (event_type IN ('CREATED', 'UPDATED', 'COMMENT',
                        'STATUS_CHANGED', 'ASSIGNMENT_CHANGED'))
);

CREATE INDEX ix_operational_case_timeline_case
    ON operational_case_timeline (case_id, occurred_at_utc DESC, id DESC);
