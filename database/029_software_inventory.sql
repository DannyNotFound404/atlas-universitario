-- Política V4 §6 Software p.16: inventario, catálogo, licencias, revisión semestral.
-- No product keys, uninstall commands, files or application usage are collected.
CREATE TABLE software_devices (
 device_id uuid PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
 snapshot_id uuid NOT NULL, captured_at_utc timestamptz NOT NULL,
 received_at_utc timestamptz NOT NULL, sources jsonb NOT NULL
);
CREATE TABLE software_products (
 id varchar(64) PRIMARY KEY, name varchar(512) NOT NULL, publisher varchar(512) NOT NULL,
 first_seen_at_utc timestamptz NOT NULL DEFAULT now(),
 license_metric varchar(16) NOT NULL DEFAULT 'UNREVIEWED' CHECK(license_metric IN ('UNREVIEWED','FREE','DEVICE','USER','OTHER')),
 category varchar(100) NOT NULL DEFAULT '', revision integer NOT NULL DEFAULT 0
);
CREATE TABLE software_installations (
 device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
 installation_key varchar(64) NOT NULL, source_id varchar(100) NOT NULL,
 product_id varchar(64) NOT NULL REFERENCES software_products(id), version varchar(128) NOT NULL,
 architecture varchar(32) NOT NULL, scope varchar(16) NOT NULL,
 installed_on date, first_seen_at_utc timestamptz NOT NULL, last_seen_at_utc timestamptz NOT NULL,
 removed_at_utc timestamptz,
 PRIMARY KEY(device_id,installation_key)
);
CREATE INDEX ix_software_installations_product ON software_installations(product_id,device_id) WHERE removed_at_utc IS NULL;
CREATE INDEX ix_software_installations_source ON software_installations(device_id,source_id) WHERE removed_at_utc IS NULL;
CREATE TABLE software_changes (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 device_id uuid NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
 snapshot_id uuid NOT NULL, product_id varchar(64) NOT NULL REFERENCES software_products(id),
 installation_key varchar(64) NOT NULL, kind varchar(16) NOT NULL CHECK(kind IN ('DETECTED','CHANGED','REMOVED')),
 old_version varchar(128), new_version varchar(128), captured_at_utc timestamptz NOT NULL,
 received_at_utc timestamptz NOT NULL,
 UNIQUE(device_id,snapshot_id,installation_key,kind)
);
CREATE INDEX ix_software_changes_date ON software_changes(received_at_utc DESC,id DESC);
CREATE TABLE software_rules (
 product_id varchar(64) NOT NULL REFERENCES software_products(id), version varchar(128) NOT NULL DEFAULT '*',
 authorization_status varchar(16) NOT NULL CHECK(authorization_status IN ('UNREVIEWED','AUTHORIZED','RESTRICTED','PROHIBITED')),
 support_ends_on date, reference varchar(1000) NOT NULL, notes varchar(2000) NOT NULL DEFAULT '',
 revision integer NOT NULL, updated_by varchar(255) NOT NULL, updated_at_utc timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(product_id,version)
);
CREATE TABLE software_licenses (
 id uuid PRIMARY KEY, product_id varchar(64) NOT NULL REFERENCES software_products(id),
 reference varchar(300) NOT NULL, metric varchar(16) NOT NULL CHECK(metric IN ('DEVICE','USER','OTHER')),
 quantity integer NOT NULL CHECK(quantity BETWEEN 1 AND 1000000),
 starts_on date NOT NULL, expires_on date, notes varchar(2000) NOT NULL DEFAULT '',
 active boolean NOT NULL DEFAULT true, revision integer NOT NULL DEFAULT 1, covered_versions text[] NOT NULL DEFAULT '{}',
 CHECK(expires_on IS NULL OR expires_on>=starts_on)
);
CREATE TABLE software_license_assignments (
 id uuid PRIMARY KEY, license_id uuid NOT NULL REFERENCES software_licenses(id),
 device_id uuid REFERENCES devices(id), subject varchar(300) NOT NULL DEFAULT '',
 assigned_by varchar(255) NOT NULL, assigned_at_utc timestamptz NOT NULL DEFAULT now(),
 released_at_utc timestamptz, CHECK(device_id IS NOT NULL OR length(subject)>0)
);
CREATE UNIQUE INDEX ux_software_license_device ON software_license_assignments(license_id,device_id) WHERE released_at_utc IS NULL AND device_id IS NOT NULL;
CREATE UNIQUE INDEX ux_software_license_subject ON software_license_assignments(license_id,lower(subject)) WHERE released_at_utc IS NULL AND device_id IS NULL;
CREATE TABLE software_actions (
 id uuid PRIMARY KEY, product_id varchar(64) REFERENCES software_products(id), device_id uuid REFERENCES devices(id),
 title varchar(300) NOT NULL, owner varchar(255) NOT NULL, due_on date NOT NULL,
 status varchar(16) NOT NULL CHECK(status IN ('OPEN','IN_PROGRESS','RESOLVED')),
 result varchar(2000) NOT NULL DEFAULT '', reference varchar(1000) NOT NULL DEFAULT '',
 revision integer NOT NULL DEFAULT 1, created_at_utc timestamptz NOT NULL DEFAULT now(), updated_at_utc timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE software_reviews (
 id uuid PRIMARY KEY, period varchar(7) NOT NULL UNIQUE CHECK(period ~ '^[0-9]{4}-S[12]$'),
 executed_by varchar(255) NOT NULL, validated_by varchar(255) NOT NULL, reference varchar(1000) NOT NULL,
 conclusion varchar(4000) NOT NULL, created_at_utc timestamptz NOT NULL,
 next_due_on date NOT NULL, evidence jsonb NOT NULL, sha256 varchar(64) NOT NULL
);
CREATE VIEW software_effective_inventory AS
 SELECT i.*, p.name, p.publisher, p.license_metric, p.category,
 d.machine_name, d.agency_code, COALESCE(d.interactive_user_display_name,d.interactive_user_account,'') AS current_user_name,
 d.interactive_user_checked_at_utc, d.enabled, d.retired_at_utc,
 COALESCE(r.authorization_status,'UNREVIEWED') AS authorization_status, r.support_ends_on, r.reference AS rule_reference,
 s.captured_at_utc AS inventory_at_utc, s.received_at_utc AS inventory_received_at_utc,
 COALESCE((SELECT bool_and(x->>'status'='COMPLETE') FROM jsonb_array_elements(s.sources) x),false) AS complete_sources
 FROM software_installations i JOIN software_products p ON p.id=i.product_id
 JOIN devices d ON d.id=i.device_id JOIN software_devices s ON s.device_id=i.device_id
 LEFT JOIN LATERAL (SELECT * FROM software_rules r WHERE r.product_id=i.product_id AND r.version IN ('*',i.version)
   ORDER BY (r.version=i.version) DESC LIMIT 1) r ON true
 WHERE i.removed_at_utc IS NULL;

CREATE TRIGGER atlas_software_review_append_only BEFORE UPDATE OR DELETE ON software_reviews
 FOR EACH ROW EXECUTE FUNCTION atlas_preserve_audit_record();
