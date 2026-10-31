ALTER TABLE admin_users
    DROP CONSTRAINT IF EXISTS ck_admin_users_role;

ALTER TABLE admin_users
    ADD CONSTRAINT ck_admin_users_role
    CHECK (role IN
        ('Administrator', 'Auditor', 'Operator', 'Installer',
         'TechnologyManager'));
