CREATE TABLE admin_notifications
(
    id               UUID PRIMARY KEY,
    fingerprint      VARCHAR(255) NOT NULL UNIQUE,
    category         VARCHAR(32) NOT NULL,
    severity         VARCHAR(16) NOT NULL,
    title            VARCHAR(160) NOT NULL,
    message          VARCHAR(800) NOT NULL,
    target_view      VARCHAR(32) NOT NULL,
    target_entity_id VARCHAR(128),
    detected_at_utc  TIMESTAMPTZ NOT NULL,
    last_seen_at_utc TIMESTAMPTZ NOT NULL
);

CREATE INDEX ix_admin_notifications_detected
    ON admin_notifications (detected_at_utc DESC);

CREATE INDEX ix_admin_notifications_category
    ON admin_notifications (category, detected_at_utc DESC);

CREATE TABLE admin_notification_reads
(
    notification_id UUID NOT NULL REFERENCES admin_notifications(id) ON DELETE CASCADE,
    username        VARCHAR(64) NOT NULL,
    read_at_utc     TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (notification_id, username)
);

CREATE INDEX ix_admin_notification_reads_user
    ON admin_notification_reads (username, read_at_utc DESC);
