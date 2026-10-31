ALTER TABLE endpoint_security_events
    ADD COLUMN severity VARCHAR(16) NOT NULL DEFAULT 'Information',
    ADD COLUMN provider VARCHAR(192) NULL,
    ADD COLUMN target_name VARCHAR(512) NULL,
    ADD COLUMN correlation_key VARCHAR(128) NULL;

CREATE INDEX ix_security_events_severity_occurred
    ON endpoint_security_events (severity, occurred_at_utc DESC);

CREATE INDEX ix_security_events_correlation
    ON endpoint_security_events (device_id, correlation_key, occurred_at_utc DESC)
    WHERE correlation_key IS NOT NULL;
