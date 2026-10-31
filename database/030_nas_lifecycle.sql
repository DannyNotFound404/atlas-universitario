-- Administrative retirement preserves resources, bindings and evidence.
ALTER TABLE nas_appliances
    ADD COLUMN lifecycle_revision bigint NOT NULL DEFAULT 0,
    ADD COLUMN retired_at_utc timestamptz,
    ADD COLUMN retired_by varchar(160),
    ADD COLUMN retirement_reason varchar(1000);

CREATE FUNCTION atlas_nas_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.code, NEW.agency_code, NEW.display_name, NEW.host, NEW.smb_port,
        NEW.share_name, NEW.manufacturer, NEW.model, NEW.serial_number,
        NEW.is_primary, NEW.enabled) IS DISTINCT FROM
       (OLD.code, OLD.agency_code, OLD.display_name, OLD.host, OLD.smb_port,
        OLD.share_name, OLD.manufacturer, OLD.model, OLD.serial_number,
        OLD.is_primary, OLD.enabled) THEN
        NEW.lifecycle_revision := OLD.lifecycle_revision + 1;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER nas_lifecycle_revision BEFORE UPDATE ON nas_appliances
    FOR EACH ROW EXECUTE FUNCTION atlas_nas_revision();

-- Lock the destination before attaching/reenabling an active dependency.
-- Unchanged historical bindings remain readable and can be detached/paused.
CREATE FUNCTION atlas_guard_active_nas_binding() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE usable boolean;
BEGIN
    IF NEW.enabled AND NEW.nas_appliance_id IS NOT NULL THEN
        IF TG_OP = 'UPDATE' THEN
            IF OLD.enabled AND OLD.nas_appliance_id IS NOT DISTINCT FROM NEW.nas_appliance_id THEN
                RETURN NEW;
            END IF;
        END IF;
        SELECT enabled INTO usable FROM nas_appliances WHERE id=NEW.nas_appliance_id FOR SHARE;
        IF usable IS DISTINCT FROM TRUE THEN
            RAISE EXCEPTION 'nas_inactive' USING ERRCODE='23514', CONSTRAINT='active_nas_binding';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER devices_active_nas_binding BEFORE INSERT OR UPDATE OF nas_appliance_id,enabled ON devices
    FOR EACH ROW EXECUTE FUNCTION atlas_guard_active_nas_binding();
CREATE TRIGGER backup_profiles_active_nas_binding BEFORE INSERT OR UPDATE OF nas_appliance_id,enabled ON backup_control_profiles
    FOR EACH ROW EXECUTE FUNCTION atlas_guard_active_nas_binding();
