-- UNIVERSITY: no se precargan agencias ni redes institucionales; conserva los registros de la VM existente.

CREATE TABLE network_scopes
(
    code                VARCHAR(64) PRIMARY KEY,
    agency_code         VARCHAR(64) NOT NULL REFERENCES agencies(code)
                            ON UPDATE CASCADE ON DELETE RESTRICT,
    display_name        VARCHAR(128) NOT NULL,
    network             CIDR NOT NULL UNIQUE,
    gateway             INET NOT NULL,
    enabled             BOOLEAN NOT NULL DEFAULT TRUE,
    scan_order          INTEGER NOT NULL,
    created_at_utc      TIMESTAMPTZ NOT NULL,
    updated_at_utc      TIMESTAMPTZ NOT NULL,
    updated_by          VARCHAR(255) NOT NULL,
    CONSTRAINT ck_network_scopes_code
        CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,63}$'),
    CONSTRAINT ck_network_scopes_ipv4
        CHECK (family(network) = 4 AND family(gateway) = 4),
    CONSTRAINT ck_network_scopes_gateway
        CHECK (gateway << network),
    CONSTRAINT ck_network_scopes_order
        CHECK (scan_order BETWEEN 1 AND 1000)
);

-- UNIVERSITY: no se precargan agencias ni redes institucionales; conserva los registros de la VM existente.

CREATE TABLE network_scan_runs
(
    id                  UUID PRIMARY KEY,
    scope_code          VARCHAR(64) NOT NULL REFERENCES network_scopes(code)
                            ON UPDATE CASCADE ON DELETE RESTRICT,
    requested_by        VARCHAR(255) NOT NULL,
    status              VARCHAR(16) NOT NULL,
    requested_at_utc    TIMESTAMPTZ NOT NULL,
    started_at_utc      TIMESTAMPTZ,
    completed_at_utc    TIMESTAMPTZ,
    addresses_targeted  INTEGER NOT NULL DEFAULT 254,
    hosts_observed      INTEGER,
    error_code          VARCHAR(64),
    error_detail        VARCHAR(512),
    CONSTRAINT ck_network_scan_status
        CHECK (status IN
            ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'TIMED_OUT', 'SKIPPED')),
    CONSTRAINT ck_network_scan_counts
        CHECK (addresses_targeted BETWEEN 1 AND 65534 AND
               (hosts_observed IS NULL OR hosts_observed >= 0))
);

CREATE INDEX ix_network_scan_runs_scope
    ON network_scan_runs (scope_code, requested_at_utc DESC);

CREATE TABLE network_assets
(
    id                  UUID PRIMARY KEY,
    scope_code          VARCHAR(64) NOT NULL REFERENCES network_scopes(code)
                            ON UPDATE CASCADE ON DELETE RESTRICT,
    ip_address          INET NOT NULL,
    hostname            VARCHAR(255),
    mac_address         VARCHAR(32),
    vendor              VARCHAR(255),
    device_kind         VARCHAR(24) NOT NULL DEFAULT 'UNKNOWN',
    display_name        VARCHAR(255),
    first_seen_at_utc   TIMESTAMPTZ NOT NULL,
    last_seen_at_utc    TIMESTAMPTZ NOT NULL,
    consecutive_misses  INTEGER NOT NULL DEFAULT 0,
    last_scan_id        UUID REFERENCES network_scan_runs(id) ON DELETE SET NULL,
    linked_device_id    UUID REFERENCES devices(id) ON DELETE SET NULL,
    linked_nas_id       UUID REFERENCES nas_appliances(id) ON DELETE SET NULL,
    classification_source VARCHAR(16) NOT NULL DEFAULT 'AUTOMATIC',
    classified_at_utc   TIMESTAMPTZ,
    classified_by       VARCHAR(255),
    CONSTRAINT ux_network_assets_scope_ip UNIQUE (scope_code, ip_address),
    CONSTRAINT ck_network_assets_ipv4 CHECK (family(ip_address) = 4),
    CONSTRAINT ck_network_assets_kind CHECK (device_kind IN
        ('ROUTER', 'NAS', 'ENDPOINT', 'PRINTER', 'ACCESS_POINT',
         'SWITCH', 'SERVER', 'CAMERA', 'UNKNOWN')),
    CONSTRAINT ck_network_assets_source CHECK
        (classification_source IN ('AUTOMATIC', 'MANUAL')),
    CONSTRAINT ck_network_assets_misses CHECK (consecutive_misses >= 0)
);

CREATE INDEX ix_network_assets_scope_seen
    ON network_assets (scope_code, last_seen_at_utc DESC);
CREATE INDEX ix_network_assets_linked_device
    ON network_assets (linked_device_id) WHERE linked_device_id IS NOT NULL;
CREATE INDEX ix_network_assets_linked_nas
    ON network_assets (linked_nas_id) WHERE linked_nas_id IS NOT NULL;
