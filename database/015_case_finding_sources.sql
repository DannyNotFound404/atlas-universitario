ALTER TABLE operational_cases
    ADD COLUMN IF NOT EXISTS source_kind VARCHAR(32),
    ADD COLUMN IF NOT EXISTS source_key VARCHAR(255),
    ADD COLUMN IF NOT EXISTS source_entity_id VARCHAR(128),
    ADD COLUMN IF NOT EXISTS source_snapshot JSONB NOT NULL DEFAULT '{}'::jsonb;

CREATE UNIQUE INDEX IF NOT EXISTS ux_operational_cases_open_source
    ON operational_cases (source_kind, source_key)
    WHERE source_kind IS NOT NULL
      AND source_key IS NOT NULL
      AND status NOT IN ('CLOSED', 'ACCEPTED_EXCEPTION');

CREATE INDEX IF NOT EXISTS ix_operational_cases_source_entity
    ON operational_cases (source_kind, source_entity_id)
    WHERE source_kind IS NOT NULL
      AND source_entity_id IS NOT NULL;
