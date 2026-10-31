-- Preserve facts at recording time. Existing rows are deliberately not backfilled.
ALTER TABLE audit_events ADD COLUMN actor_display_name_snapshot TEXT,
    ADD COLUMN target_display_name_snapshot TEXT,
    ADD COLUMN context_captured BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE enrollment_tokens ADD COLUMN issued_by_display_name_snapshot TEXT,
    ADD COLUMN agency_name_snapshot TEXT;
ALTER TABLE device_enrollment_events ADD COLUMN issued_by_display_name_snapshot TEXT,
    ADD COLUMN agency_name_snapshot TEXT;

CREATE FUNCTION atlas_capture_audit_context() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE target_name TEXT;
BEGIN
    IF NEW.actor_type IN ('Administrator','Auditor','TechnologyManager','Installer','Operator','Admin') THEN
        SELECT display_name INTO NEW.actor_display_name_snapshot FROM admin_users
            WHERE normalized_username = UPPER(NEW.actor_id);
    ELSIF NEW.actor_type = 'Agent' THEN
        SELECT machine_name INTO NEW.actor_display_name_snapshot FROM devices WHERE id::text = NEW.actor_id;
    END IF;
    CASE NEW.target_type
        WHEN 'Device' THEN SELECT machine_name INTO target_name FROM devices WHERE id::text=NEW.target_id;
        WHEN 'AdminUser' THEN SELECT display_name INTO target_name FROM admin_users WHERE id::text=NEW.target_id;
        WHEN 'Agency' THEN SELECT display_name INTO target_name FROM agencies WHERE code=NEW.target_id;
        WHEN 'NasAppliance' THEN SELECT display_name INTO target_name FROM nas_appliances WHERE id::text=NEW.target_id;
        WHEN 'GovernanceDocument' THEN SELECT title INTO target_name FROM governance_documents WHERE id::text=NEW.target_id;
        WHEN 'GovernanceDocumentVersion' THEN SELECT original_file_name INTO target_name FROM governance_document_versions WHERE id::text=NEW.target_id;
        WHEN 'BackupControlProfile' THEN SELECT system_name INTO target_name FROM backup_control_profiles WHERE id::text=NEW.target_id;
        WHEN 'BackupPlan' THEN SELECT name INTO target_name FROM backup_control_plans WHERE id::text=NEW.target_id;
        WHEN 'OperationalCase' THEN SELECT title INTO target_name FROM operational_cases WHERE id::text=NEW.target_id;
        WHEN 'TrainingCycle' THEN SELECT name INTO target_name FROM training_cycles WHERE id::text=NEW.target_id;
        WHEN 'TrainingSession' THEN SELECT title INTO target_name FROM training_sessions WHERE id::text=NEW.target_id;
        WHEN 'TrainingTopic' THEN SELECT title INTO target_name FROM training_topics WHERE id::text=NEW.target_id;
        WHEN 'TrainingParticipant' THEN SELECT display_name INTO target_name FROM training_participants WHERE id::text=NEW.target_id;
        ELSE target_name := NULL;
    END CASE;
    NEW.target_display_name_snapshot := target_name;
    NEW.context_captured := TRUE;
    RETURN NEW;
END $$;
CREATE TRIGGER atlas_audit_context BEFORE INSERT ON audit_events
    FOR EACH ROW EXECUTE FUNCTION atlas_capture_audit_context();

CREATE FUNCTION atlas_capture_issuance_context() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.issuance_source = 'DASHBOARD' THEN
        SELECT display_name INTO NEW.issued_by_display_name_snapshot FROM admin_users
            WHERE normalized_username = UPPER(NEW.issued_by);
    END IF;
    SELECT display_name INTO NEW.agency_name_snapshot FROM agencies WHERE code = NEW.agency_code;
    RETURN NEW;
END $$;
CREATE TRIGGER atlas_issuance_context BEFORE INSERT ON enrollment_tokens
    FOR EACH ROW EXECUTE FUNCTION atlas_capture_issuance_context();

-- Protect the original record from normal SQL update/delete as well as the UI.
-- Database owners can still perform explicit maintenance; this is not a claim of tamper-proof storage.
CREATE FUNCTION atlas_preserve_audit_record() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ATLAS audit evidence is append-only' USING ERRCODE='55000';
END $$;
CREATE TRIGGER atlas_audit_append_only BEFORE UPDATE OR DELETE ON audit_events
    FOR EACH ROW EXECUTE FUNCTION atlas_preserve_audit_record();
CREATE TRIGGER atlas_enrollment_append_only BEFORE UPDATE OR DELETE ON device_enrollment_events
    FOR EACH ROW EXECUTE FUNCTION atlas_preserve_audit_record();
