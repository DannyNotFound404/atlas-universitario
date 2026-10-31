ALTER TABLE devices
    DROP CONSTRAINT ck_devices_backup_provider;

ALTER TABLE devices
    ADD CONSTRAINT ck_devices_backup_provider
        CHECK
        (
            backup_provider IN
            (
                'ATLAS',
                'ACRONIS',
                'EXTERNAL',
                'SHARED_FOLDER'
            )
        );
