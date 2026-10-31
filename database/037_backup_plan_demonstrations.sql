-- V4 pp. 9-10 / reference base 7.1-7.2: documented recovery evidence.
-- Three distinct samples is an operational criterion, not statistical assurance.
-- Existing individual restorations and their evidence remain unchanged.
CREATE TABLE backup_plan_demonstrations (
    id uuid PRIMARY KEY,
    plan_id uuid NOT NULL REFERENCES backup_control_plans(id),
    plan_revision integer NOT NULL,
    configuration jsonb NOT NULL,
    population_count integer NOT NULL CHECK (population_count > 0),
    required_samples integer NOT NULL CHECK (required_samples BETWEEN 1 AND 3),
    notes varchar(4000),
    created_at_utc timestamptz NOT NULL,
    created_by varchar(160) NOT NULL,
    FOREIGN KEY(plan_id,plan_revision) REFERENCES backup_control_plan_versions(plan_id,revision)
);
CREATE INDEX ix_backup_plan_demonstrations_plan ON backup_plan_demonstrations(plan_id,created_at_utc DESC);
CREATE TABLE backup_plan_demonstration_samples (
    id uuid PRIMARY KEY,
    attempt_order bigint GENERATED ALWAYS AS IDENTITY,
    demonstration_id uuid NOT NULL REFERENCES backup_plan_demonstrations(id),
    profile_id uuid NOT NULL REFERENCES backup_control_profiles(id),
    restoration_id uuid NOT NULL UNIQUE REFERENCES backup_restoration_tests(id),
    snapshot jsonb NOT NULL,
    repeats_sample_id uuid UNIQUE REFERENCES backup_plan_demonstration_samples(id),
    created_at_utc timestamptz NOT NULL
);
CREATE UNIQUE INDEX ux_backup_demonstration_sample_root ON backup_plan_demonstration_samples(demonstration_id,profile_id) WHERE repeats_sample_id IS NULL;
CREATE INDEX ix_backup_demonstration_attempts ON backup_plan_demonstration_samples(demonstration_id,attempt_order);
CREATE TABLE backup_plan_demonstration_requests (
    id uuid PRIMARY KEY,
    demonstration_id uuid NOT NULL REFERENCES backup_plan_demonstrations(id),
    fingerprint char(64) NOT NULL,
    evidence_id uuid REFERENCES backup_control_evidence(id),
    created_at_utc timestamptz NOT NULL
);
-- Latest attempt per actual tested profile; earlier failed attempts are retained.
CREATE VIEW backup_plan_demonstration_effective_samples AS
SELECT DISTINCT ON (s.demonstration_id,s.profile_id)
    s.*,r.result,r.finished_at_utc
FROM backup_plan_demonstration_samples s
JOIN backup_restoration_tests r ON r.id=s.restoration_id
ORDER BY s.demonstration_id,s.profile_id,s.attempt_order DESC;
CREATE VIEW backup_plan_demonstration_results AS
SELECT d.id,d.plan_id,d.plan_revision,d.required_samples,d.population_count,d.created_at_utc,
    COUNT(s.id)::integer AS tested,
    COUNT(s.id) FILTER(WHERE s.result='SUCCESS')::integer AS successful,
    COUNT(s.id) FILTER(WHERE s.result<>'SUCCESS')::integer AS unresolved,
    MAX(s.finished_at_utc) AS finished_at_utc,
    MAX(s.finished_at_utc) + make_interval(days => (d.configuration->>'restoreTestIntervalDays')::integer) AS next_due_utc
FROM backup_plan_demonstrations d
LEFT JOIN backup_plan_demonstration_effective_samples s ON s.demonstration_id=d.id
GROUP BY d.id;
-- An unresolved individual test must not disappear when its profile joins a plan.
CREATE VIEW backup_plan_legacy_restoration_failures AS
SELECT p.plan_id,p.id AS profile_id,p.system_name,r.result,r.finished_at_utc
FROM backup_control_profiles p
LEFT JOIN devices d ON d.id=p.device_id
LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
JOIN LATERAL (
    SELECT t.* FROM backup_restoration_tests t WHERE t.profile_id=p.id
    AND (c.tracking_since_utc IS NULL OR t.finished_at_utc>=c.tracking_since_utc)
    ORDER BY t.finished_at_utc DESC,t.created_at_utc DESC,t.id DESC LIMIT 1
) r ON true
WHERE p.plan_id IS NOT NULL AND p.enabled AND p.approval_status IN ('ACTIVE','APPROVED')
AND NOT COALESCE(c.backups_paused,false) AND (p.device_id IS NULL OR d.enabled)
AND r.result<>'SUCCESS' AND NOT EXISTS(SELECT 1 FROM backup_plan_demonstration_samples s WHERE s.restoration_id=r.id);
