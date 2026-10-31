ALTER TABLE network_scopes
    ADD COLUMN automatic_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN interval_minutes INTEGER NOT NULL DEFAULT 30 CHECK (interval_minutes BETWEEN 5 AND 10080),
    ADD COLUMN identification_interval_hours INTEGER NOT NULL DEFAULT 24 CHECK (identification_interval_hours BETWEEN 1 AND 168),
    ADD COLUMN excluded_addresses TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN next_scan_at_utc TIMESTAMPTZ,
    ADD COLUMN configuration_revision INTEGER NOT NULL DEFAULT 1;

ALTER TABLE network_scan_runs DROP CONSTRAINT ck_network_scan_status;
ALTER TABLE network_scan_runs
    ADD CONSTRAINT ck_network_scan_status CHECK (status IN
        ('QUEUED','RUNNING','SUCCEEDED','PARTIAL','FAILED','TIMED_OUT','SKIPPED','INTERRUPTED','CANCELLED')),
    ADD COLUMN lease_owner UUID,
    ADD COLUMN lease_expires_at_utc TIMESTAMPTZ,
    ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN retry_after_at_utc TIMESTAMPTZ,
    ADD COLUMN configuration_revision INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN discovery_complete BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN identification_status VARCHAR(16) NOT NULL DEFAULT 'NOT_RUN',
    ADD COLUMN warnings JSONB NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN methods TEXT[] NOT NULL DEFAULT '{}';

-- Old in-memory jobs cannot still own a process after this deployment stops the service.
UPDATE network_scan_runs SET status='INTERRUPTED',completed_at_utc=CURRENT_TIMESTAMP,
    error_code='upgrade_interrupted',error_detail='Sondeo anterior interrumpido durante la actualización.'
WHERE status='RUNNING';
WITH duplicates AS (
    SELECT id,row_number() OVER(PARTITION BY scope_code ORDER BY requested_at_utc DESC,id) n
    FROM network_scan_runs WHERE status='QUEUED'
)
UPDATE network_scan_runs SET status='SKIPPED',completed_at_utc=CURRENT_TIMESTAMP,error_code='duplicate_job'
WHERE id IN (SELECT id FROM duplicates WHERE n>1);
CREATE UNIQUE INDEX ux_network_scan_active_scope ON network_scan_runs(scope_code)
    WHERE status IN ('QUEUED','RUNNING');
CREATE INDEX ix_network_scan_queue ON network_scan_runs(requested_at_utc,id) WHERE status='QUEUED';

ALTER TABLE network_assets
    ADD COLUMN presence_sources TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN identification_basis VARCHAR(32) NOT NULL DEFAULT 'LEGACY',
    ADD COLUMN services_checked_at_utc TIMESTAMPTZ,
    ADD COLUMN services_check_status VARCHAR(16) NOT NULL DEFAULT 'NOT_RUN',
    ADD COLUMN last_identified_device_id UUID REFERENCES devices(id) ON DELETE SET NULL,
    ADD COLUMN retired_at_utc TIMESTAMPTZ;
UPDATE network_assets SET services_checked_at_utc=enriched_at_utc,
    last_identified_device_id=linked_device_id,
    services_check_status=CASE WHEN enriched_at_utc IS NULL THEN 'NOT_RUN' ELSE 'HISTORICAL' END;
-- Keep the original scope/IP unique constraint so a previous application can
-- still record scans after an application-only rollback.
CREATE TABLE network_asset_identities(id UUID PRIMARY KEY);
INSERT INTO network_asset_identities SELECT id FROM network_assets;
CREATE TABLE network_asset_archives(
    asset_id UUID PRIMARY KEY REFERENCES network_asset_identities(id) ON DELETE RESTRICT,
    archived_at_utc TIMESTAMPTZ NOT NULL,
    snapshot JSONB NOT NULL
);

CREATE TABLE network_asset_events (
    id UUID PRIMARY KEY,
    asset_id UUID NOT NULL REFERENCES network_asset_identities(id) ON DELETE RESTRICT,
    run_id UUID REFERENCES network_scan_runs(id) ON DELETE RESTRICT,
    event_type VARCHAR(32) NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,
    evidence JSONB NOT NULL
);
CREATE INDEX ix_network_asset_events ON network_asset_events(asset_id,occurred_at_utc DESC);
CREATE FUNCTION atlas_network_evidence_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'La evidencia de red es de solo incorporación.' USING ERRCODE='55000';
END;
$$;
CREATE TRIGGER network_asset_events_immutable BEFORE UPDATE OR DELETE ON network_asset_events
    FOR EACH ROW EXECUTE FUNCTION atlas_network_evidence_immutable();
CREATE TRIGGER network_asset_archives_immutable BEFORE UPDATE OR DELETE ON network_asset_archives
    FOR EACH ROW EXECUTE FUNCTION atlas_network_evidence_immutable();
