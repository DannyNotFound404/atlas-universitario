-- Delivery metadata only; rejected payloads and operational secrets remain local.
ALTER TABLE devices ADD COLUMN agent_delivery_health jsonb;
ALTER TABLE devices ADD COLUMN agent_delivery_health_received_at_utc timestamptz;
ALTER TABLE devices ADD CONSTRAINT ck_agent_delivery_health_object
    CHECK(agent_delivery_health IS NULL OR jsonb_typeof(agent_delivery_health)='object');
