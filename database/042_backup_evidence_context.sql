-- V4 pp. 9–10; base §7.2/§11. Aplicación anticipada, vigencia formal 2027-06-01.
-- A historical copy keeps its original date/result. No context is invented for legacy evidence.
CREATE OR REPLACE FUNCTION atlas_backup_context_snapshot(p backup_control_profiles) RETURNS jsonb
LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object(
    'deviceId',p.device_id,'systemIdentity',CASE WHEN p.device_id IS NULL THEN btrim(p.system_name) ELSE NULL END,
    'agency',p.agency_code,'scopeType',p.scope_type,'provider',p.provider,
    'task',COALESCE(btrim(p.task_name),''),
    'destination',CASE WHEN p.provider='ATLAS_BACKUPNAS' THEN
      jsonb_build_object('host',lower(n.host),'share',lower(n.share_name))
      ELSE to_jsonb(lower(btrim(COALESCE(p.expected_destination,'')))) END,
    'folders',COALESCE((SELECT jsonb_agg(folder ORDER BY folder) FROM
      (SELECT DISTINCT atlas_backup_folder_key(f) folder FROM unnest(p.protected_folders) f) x),'[]'::jsonb),
    'unstructuredScope',CASE WHEN cardinality(p.protected_folders)=0 THEN btrim(p.protected_scope) ELSE NULL END,
    'exclusions',COALESCE(btrim(p.protected_scope_exclusions),'')
  ) FROM (SELECT 1) seed LEFT JOIN nas_appliances n ON n.id=p.nas_appliance_id;
$$;
CREATE OR REPLACE FUNCTION atlas_backup_context_key(p backup_control_profiles) RETURNS text
LANGUAGE sql STABLE AS $$ SELECT encode(sha256(convert_to(atlas_backup_context_snapshot(p)::text,'UTF8')),'hex'); $$;

ALTER TABLE backup_control_observations ADD COLUMN context_snapshot jsonb, ADD COLUMN context_key char(64);
ALTER TABLE backup_restoration_tests ADD COLUMN context_snapshot jsonb, ADD COLUMN context_key char(64);
CREATE INDEX ix_backup_observation_context ON backup_control_observations(profile_id,context_key,period_end_utc DESC);
CREATE INDEX ix_backup_restoration_context ON backup_restoration_tests(profile_id,context_key,finished_at_utc DESC);

CREATE TABLE backup_context_review_requests(
 id uuid PRIMARY KEY, fingerprint char(64) NOT NULL, created_at_utc timestamptz NOT NULL,
 created_by varchar(160) NOT NULL, reason varchar(2000) NOT NULL
);
CREATE TABLE backup_evidence_context_validations(
 id uuid PRIMARY KEY, request_id uuid NOT NULL REFERENCES backup_context_review_requests(id),
 profile_id uuid NOT NULL REFERENCES backup_control_profiles(id),
 record_kind varchar(16) NOT NULL CHECK(record_kind IN ('OBSERVATION','RESTORATION')), record_id uuid NOT NULL,
 context_key char(64) NOT NULL, context_snapshot jsonb NOT NULL,
 validated_at_utc timestamptz NOT NULL, validated_by varchar(160) NOT NULL,
 UNIQUE(record_kind,record_id)
);
CREATE INDEX ix_backup_evidence_validation_context ON backup_evidence_context_validations(profile_id,context_key,record_kind,record_id);
CREATE TRIGGER backup_context_review_immutable BEFORE UPDATE OR DELETE ON backup_context_review_requests
 FOR EACH ROW EXECUTE FUNCTION atlas_preserve_audit_record();
CREATE TRIGGER backup_evidence_context_validation_immutable BEFORE UPDATE OR DELETE ON backup_evidence_context_validations
 FOR EACH ROW EXECUTE FUNCTION atlas_preserve_audit_record();
CREATE OR REPLACE FUNCTION atlas_backup_evidence_context_matches(p backup_control_profiles,p_kind text,p_record_id uuid,p_recorded_key text)
RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT COALESCE(p_recorded_key=atlas_backup_context_key(p),false) OR EXISTS(
   SELECT 1 FROM backup_evidence_context_validations v WHERE v.profile_id=p.id
     AND v.record_kind=p_kind AND v.record_id=p_record_id AND v.context_key=atlas_backup_context_key(p));
$$;
