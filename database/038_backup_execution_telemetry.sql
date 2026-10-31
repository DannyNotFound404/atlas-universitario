-- V4 pp. 9-10: execution evidence; pp. 21-22: protected centralized records.
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS received_at_utc TIMESTAMPTZ;
ALTER TABLE backup_runs ADD COLUMN IF NOT EXISTS acceptable BOOLEAN NOT NULL DEFAULT FALSE;
-- Legacy success remains legacy evidence. Never reinterpret historical warnings as complete.
UPDATE backup_runs SET acceptable = TRUE
WHERE payload->'evidence' IS NULL AND status = 'Successful'
  AND COALESCE(jsonb_array_length(payload->'failedFolders'), 0) = 0
  AND COALESCE(jsonb_array_length(payload->'missingFolders'), 0) = 0;
CREATE INDEX IF NOT EXISTS ix_backup_runs_acceptable ON backup_runs(device_id, finished_at_utc DESC) WHERE acceptable;
-- Compare only explicit folder names/paths and the two supported profile aliases.
-- Never guess that an arbitrary path belongs to a different user's profile.
CREATE OR REPLACE FUNCTION atlas_backup_folder_key(value text) RETURNS text
LANGUAGE sql IMMUTABLE STRICT AS $$
    SELECT CASE lower(btrim(value, E' \\/'))
      WHEN 'escritorio' THEN 'desktop' WHEN 'escritorio del usuario' THEN 'desktop'
      WHEN 'documentos' THEN 'documents' WHEN 'documentos del usuario' THEN 'documents'
      ELSE lower(replace(btrim(value, E' \\/'), E'\\', '/')) END;
$$;
CREATE OR REPLACE FUNCTION atlas_backup_scope_matches(expected text[], payload jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN payload IS NULL THEN false
      WHEN payload->'evidence' IS NULL OR payload->'evidence'='null'::jsonb THEN true
      ELSE COALESCE(cardinality(expected),0)>0 AND NOT EXISTS (
        SELECT 1 FROM unnest(expected) f WHERE NOT EXISTS (
          SELECT 1 FROM jsonb_array_elements(COALESCE(payload#>'{evidence,folders}','[]'::jsonb)) actual
          WHERE atlas_backup_folder_key(f)=atlas_backup_folder_key(actual->>'name')
            AND actual->>'state'='COMPLETED'
        )
      ) END;
$$;
