-- V4: inventory pp.6/16, access pp.13-15/23-26, backups pp.9-10.
-- Administrative custody does not change enrollment, credentials or backup jobs.
CREATE TABLE asset_custody (
    device_id uuid PRIMARY KEY REFERENCES devices(id),
    current_device_id uuid NOT NULL UNIQUE REFERENCES devices(id),
    revision bigint NOT NULL CHECK (revision > 0),
    state varchar(20) NOT NULL CHECK (state IN ('IN_USE','RECEIVED','PREPARING','AVAILABLE')),
    assigned_to varchar(160),
    custodian varchar(160) NOT NULL,
    location varchar(200) NOT NULL,
    receipt jsonb NOT NULL DEFAULT '{}',
    preparation jsonb NOT NULL DEFAULT '{}',
    backups_paused boolean NOT NULL DEFAULT false,
    pause_reason varchar(1000),
    review_due_utc timestamptz,
    tracking_since_utc timestamptz,
    updated_at_utc timestamptz NOT NULL,
    updated_by varchar(160) NOT NULL,
    CHECK ((state='IN_USE' AND assigned_to IS NOT NULL AND NOT backups_paused)
        OR (state<>'IN_USE' AND assigned_to IS NULL)),
    CHECK (NOT backups_paused OR (pause_reason IS NOT NULL AND review_due_utc IS NOT NULL))
);
CREATE INDEX ix_asset_custody_state ON asset_custody(state,review_due_utc);
CREATE TABLE asset_custody_identities (
    device_id uuid PRIMARY KEY REFERENCES devices(id),
    asset_id uuid NOT NULL REFERENCES asset_custody(device_id)
);
CREATE TABLE asset_custody_events (
    id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES devices(id),
    revision bigint NOT NULL,
    action varchar(30) NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    actor varchar(160) NOT NULL,
    detail jsonb NOT NULL,
    UNIQUE(device_id,revision)
);
CREATE INDEX ix_asset_custody_events_device ON asset_custody_events(device_id,revision DESC);
CREATE FUNCTION protect_asset_custody_history() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Asset custody history is append-only' USING ERRCODE='23514', CONSTRAINT='immutable_asset_custody_history';
END; $$;
CREATE TRIGGER asset_custody_history_immutable BEFORE UPDATE OR DELETE ON asset_custody_events
FOR EACH ROW EXECUTE FUNCTION protect_asset_custody_history();
