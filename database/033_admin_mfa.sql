CREATE TABLE atlas_admin_mfa (
    user_id uuid PRIMARY KEY REFERENCES admin_users(id),
    enabled boolean NOT NULL DEFAULT false,
    protected_secret text,
    pending_secret text,
    pending_until_utc timestamptz,
    last_step bigint NOT NULL DEFAULT -1,
    recovery_hashes text[] NOT NULL DEFAULT '{}',
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK(NOT enabled OR protected_secret IS NOT NULL)
);
