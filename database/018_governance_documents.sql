CREATE TABLE governance_documents
(
    id                  UUID PRIMARY KEY,
    code                VARCHAR(64) NOT NULL UNIQUE,
    title               VARCHAR(220) NOT NULL,
    document_type       VARCHAR(24) NOT NULL,
    summary             TEXT NOT NULL,
    objective           TEXT,
    scope               TEXT,
    owner_name          VARCHAR(160) NOT NULL,
    approver_name       VARCHAR(160),
    status              VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    parent_document_id  UUID REFERENCES governance_documents(id)
                            ON DELETE RESTRICT,
    effective_date      DATE,
    next_review_date    DATE,
    created_at_utc      TIMESTAMPTZ NOT NULL,
    created_by          VARCHAR(64) NOT NULL,
    updated_at_utc      TIMESTAMPTZ NOT NULL,
    updated_by          VARCHAR(64) NOT NULL,
    CONSTRAINT ck_governance_documents_type
        CHECK (document_type IN
            ('MASTER_POLICY', 'POLICY', 'PROCEDURE', 'STANDARD',
             'GUIDE', 'FORM', 'ANNEX')),
    CONSTRAINT ck_governance_documents_status
        CHECK (status IN ('DRAFT', 'IN_REVIEW', 'APPROVED', 'OBSOLETE')),
    CONSTRAINT ck_governance_documents_dates
        CHECK (next_review_date IS NULL OR effective_date IS NULL OR
               next_review_date >= effective_date),
    CONSTRAINT ck_governance_documents_approval
        CHECK (status <> 'APPROVED' OR
               (approver_name IS NOT NULL AND effective_date IS NOT NULL AND
                next_review_date IS NOT NULL)),
    CONSTRAINT ck_governance_documents_parent
        CHECK (parent_document_id IS NULL OR parent_document_id <> id)
);

CREATE INDEX ix_governance_documents_status
    ON governance_documents (status, next_review_date, title);

CREATE INDEX ix_governance_documents_parent
    ON governance_documents (parent_document_id, document_type, title);

CREATE TABLE governance_document_versions
(
    id                    UUID PRIMARY KEY,
    document_id           UUID NOT NULL REFERENCES governance_documents(id)
                              ON DELETE RESTRICT,
    version_label         VARCHAR(32) NOT NULL,
    original_file_name    VARCHAR(255) NOT NULL,
    media_type            VARCHAR(120) NOT NULL,
    stored_relative_path  VARCHAR(500) NOT NULL UNIQUE,
    size_bytes            BIGINT NOT NULL,
    sha256                CHAR(64) NOT NULL,
    change_summary        TEXT NOT NULL,
    is_current            BOOLEAN NOT NULL DEFAULT TRUE,
    published_at_utc      TIMESTAMPTZ,
    published_by          VARCHAR(64),
    created_at_utc        TIMESTAMPTZ NOT NULL,
    created_by            VARCHAR(64) NOT NULL,
    CONSTRAINT ux_governance_document_version
        UNIQUE (document_id, version_label),
    CONSTRAINT ck_governance_document_version_size
        CHECK (size_bytes BETWEEN 1 AND 25000000),
    CONSTRAINT ck_governance_document_version_hash
        CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_governance_document_version_publish
        CHECK ((published_at_utc IS NULL) = (published_by IS NULL))
);

CREATE UNIQUE INDEX ux_governance_document_current_version
    ON governance_document_versions (document_id)
    WHERE is_current;

CREATE INDEX ix_governance_document_versions_history
    ON governance_document_versions (document_id, created_at_utc DESC);

CREATE TABLE governance_document_links
(
    source_document_id  UUID NOT NULL REFERENCES governance_documents(id)
                            ON DELETE CASCADE,
    target_document_id  UUID NOT NULL REFERENCES governance_documents(id)
                            ON DELETE RESTRICT,
    relationship        VARCHAR(24) NOT NULL DEFAULT 'RELATED',
    created_at_utc      TIMESTAMPTZ NOT NULL,
    created_by          VARCHAR(64) NOT NULL,
    PRIMARY KEY (source_document_id, target_document_id),
    CONSTRAINT ck_governance_document_link_self
        CHECK (source_document_id <> target_document_id),
    CONSTRAINT ck_governance_document_link_relationship
        CHECK (relationship IN ('RELATED', 'IMPLEMENTS', 'SUPPORTS'))
);

CREATE INDEX ix_governance_document_links_target
    ON governance_document_links (target_document_id, source_document_id);
