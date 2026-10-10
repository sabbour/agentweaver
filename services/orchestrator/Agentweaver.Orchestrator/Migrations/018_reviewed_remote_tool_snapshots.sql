CREATE TABLE {schema}.reviewed_remote_tool_snapshots (
    project_id varchar(512) NOT NULL,
    snapshot_id uuid NOT NULL,
    snapshot_digest char(64) NOT NULL,
    agent_id varchar(512) NOT NULL,
    node_id varchar(512) NOT NULL,
    snapshot_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, snapshot_id),
    CHECK (snapshot_digest ~ '^[0-9a-f]{64}$'),
    CHECK (
        jsonb_typeof(snapshot_json) = 'object' AND
        snapshot_json ?& ARRAY['snapshotId', 'projectId', 'agentId', 'nodeId'] AND
        jsonb_typeof(snapshot_json->'snapshotId') = 'string' AND
        jsonb_typeof(snapshot_json->'projectId') = 'string' AND
        jsonb_typeof(snapshot_json->'agentId') = 'string' AND
        jsonb_typeof(snapshot_json->'nodeId') = 'string' AND
        snapshot_json->>'snapshotId' = snapshot_id::text AND
        snapshot_json->>'projectId' = project_id AND
        snapshot_json->>'agentId' = agent_id AND
        snapshot_json->>'nodeId' = node_id
    )
);

CREATE INDEX ix_reviewed_remote_tool_snapshots_project_agent_node
    ON {schema}.reviewed_remote_tool_snapshots
        (project_id, agent_id, node_id, created_at DESC, snapshot_id);

CREATE OR REPLACE FUNCTION {schema}.reject_reviewed_remote_tool_snapshot_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Reviewed remote tool snapshots are immutable';
END
$$;

CREATE TRIGGER reviewed_remote_tool_snapshots_immutable
    BEFORE UPDATE OR DELETE ON {schema}.reviewed_remote_tool_snapshots
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_reviewed_remote_tool_snapshot_mutation();
