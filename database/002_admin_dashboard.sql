CREATE TABLE IF NOT EXISTS admin_users
(
    id                  UUID PRIMARY KEY,
    username            VARCHAR(64) NOT NULL,
    normalized_username VARCHAR(64) NOT NULL UNIQUE,
    display_name        VARCHAR(128) NOT NULL,
    password_hash       TEXT NOT NULL,
    role                VARCHAR(32) NOT NULL,
    enabled             BOOLEAN NOT NULL DEFAULT TRUE,
    failed_attempts     INTEGER NOT NULL DEFAULT 0,
    locked_until_utc    TIMESTAMPTZ,
    created_at_utc      TIMESTAMPTZ NOT NULL,
    last_login_at_utc   TIMESTAMPTZ,
    CONSTRAINT ck_admin_users_failed_attempts_nonnegative
        CHECK (failed_attempts >= 0),
    CONSTRAINT ck_admin_users_role
        CHECK (role IN ('Administrator', 'Auditor', 'Operator'))
);

CREATE INDEX IF NOT EXISTS ix_admin_users_enabled
    ON admin_users (enabled);
