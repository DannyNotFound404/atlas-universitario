CREATE TABLE atlas_agent_policies (
    revision bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope varchar(16) NOT NULL CHECK(scope IN ('INSTITUTION','AGENCY','DEVICE')),
    target varchar(64) NOT NULL,
    values jsonb NOT NULL,
    sha256 char(64) NOT NULL,
    actor varchar(64) NOT NULL,
    reason varchar(500) NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX atlas_agent_policy_scope ON atlas_agent_policies(scope,target,revision DESC);
CREATE TRIGGER atlas_agent_policy_history_immutable BEFORE UPDATE OR DELETE ON atlas_agent_policies FOR EACH ROW EXECUTE FUNCTION atlas_evidence_immutable();
CREATE TABLE atlas_agent_policy_status (
    device_id uuid PRIMARY KEY REFERENCES devices(id),
    protocol integer NOT NULL,
    desired_revision bigint NOT NULL,
    desired_sha256 char(64) NOT NULL,
    applied_revision bigint,
    status varchar(16) NOT NULL CHECK(status IN ('RECEIVED','APPLIED','REJECTED')),
    error_code varchar(64),
    checked_at_utc timestamptz NOT NULL,
    applied_at_utc timestamptz
);
