ALTER TABLE network_assets
    ADD COLUMN services JSONB NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN operating_system_hint VARCHAR(255),
    ADD COLUMN device_type_hint VARCHAR(96),
    ADD COLUMN identification_confidence SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN enriched_at_utc TIMESTAMPTZ;

ALTER TABLE network_assets
    ADD CONSTRAINT ck_network_assets_services_array
        CHECK (jsonb_typeof(services) = 'array'),
    ADD CONSTRAINT ck_network_assets_identification_confidence
        CHECK (identification_confidence BETWEEN 0 AND 100);

CREATE INDEX ix_network_assets_services
    ON network_assets USING GIN (services jsonb_path_ops);

