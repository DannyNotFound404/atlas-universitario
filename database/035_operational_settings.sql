CREATE TABLE atlas_evidence_holds (
    id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES devices(id),
    case_id uuid NOT NULL REFERENCES operational_cases(id),
    from_utc timestamptz NOT NULL,
    to_utc timestamptz NOT NULL,
    reason varchar(500) NOT NULL,
    created_by varchar(64) NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    released_at_utc timestamptz,
    released_by varchar(64),
    release_reason varchar(500),
    CHECK(to_utc>from_utc)
);
CREATE INDEX atlas_evidence_holds_active ON atlas_evidence_holds(device_id,from_utc,to_utc) WHERE released_at_utc IS NULL;
CREATE TABLE atlas_notification_mutes (
    id uuid PRIMARY KEY,
    category varchar(32) NOT NULL CHECK(category IN ('BACKUP','BACKUP_CONTROL','POSTURE','SECURITY','SOFTWARE','ASSET_CUSTODY')),
    until_utc timestamptz NOT NULL,
    reason varchar(500) NOT NULL,
    created_by varchar(64) NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    released_at_utc timestamptz
);
CREATE VIEW atlas_visible_notifications AS SELECT n.* FROM admin_notifications n
WHERE NOT EXISTS(SELECT 1 FROM atlas_notification_mutes m WHERE m.category=n.category AND m.released_at_utc IS NULL AND m.until_utc>CURRENT_TIMESTAMP);
