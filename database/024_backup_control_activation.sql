-- Operational activation is distinct from the historical approval workflow.
-- Existing records and their approval evidence are not rewritten.
ALTER TABLE backup_control_profiles
    DROP CONSTRAINT ck_backup_control_profile_approval;

ALTER TABLE backup_control_profiles
    ADD CONSTRAINT ck_backup_control_profile_approval
        CHECK (approval_status IN
            ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'RETURNED', 'ACTIVE'));

ALTER TABLE backup_control_profiles
    ADD CONSTRAINT ck_backup_control_profile_active
        CHECK (approval_status <> 'ACTIVE' OR
               (enabled AND exception_type <> 'NORMATIVE'));

COMMENT ON COLUMN backup_control_profiles.approval_status IS
    'ACTIVE: seguimiento operativo; APPROVED: aprobación histórica anterior a 0.9.30-rc.2. DRAFT/PENDING_APPROVAL/RETURNED: sin seguimiento.';
