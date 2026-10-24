-- Inventory rows are committed before Object Store writes. A missing retention deadline
-- marks an unreferenced write until the journal records its reference.
CREATE TABLE {schema}.session_object_inventory (
    object_key varchar(1024) PRIMARY KEY,
    tracked_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    retain_until timestamptz,
    deleted_at timestamptz
);

CREATE INDEX ix_session_object_inventory_retention
    ON {schema}.session_object_inventory (retain_until)
    WHERE retain_until IS NOT NULL AND deleted_at IS NULL;

CREATE INDEX ix_session_object_inventory_pending
    ON {schema}.session_object_inventory (tracked_at)
    WHERE retain_until IS NULL AND deleted_at IS NULL;

CREATE INDEX ix_session_object_references_object_key
    ON {schema}.session_object_references (object_key);

INSERT INTO {schema}.session_object_inventory (object_key, tracked_at, retain_until)
SELECT object_key, clock_timestamp(), max(retain_until)
FROM {schema}.session_object_references
GROUP BY object_key
ON CONFLICT (object_key) DO NOTHING;
