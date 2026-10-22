-- Preserve every link and its original receipt. Legacy rows keep a NULL identity.
-- The migrator runs this forward-only change under its transaction and schema lock.
-- ALTER TABLE and index creation block link writes while validating existing rows.
ALTER TABLE {schema}.chief_of_staff_priority_links
    ADD COLUMN provider_id varchar(256),
    ADD COLUMN repository_owner varchar(256),
    ADD COLUMN repository_name varchar(256),
    ADD CONSTRAINT ck_chief_of_staff_priority_links_repository_identity CHECK (
        (provider_id IS NULL AND repository_owner IS NULL AND repository_name IS NULL) OR
        (kind IN ('issue', 'pull_request') AND
            provider_id IS NOT NULL AND length(provider_id) > 0 AND
            repository_owner IS NOT NULL AND length(repository_owner) > 0 AND
            repository_name IS NOT NULL AND length(repository_name) > 0)
    );

-- Check the complete replacement key before removing any existing protection.
-- A corrupt or conflicting history aborts the transaction instead of discarding rows.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM {schema}.chief_of_staff_priority_links
        GROUP BY priority_id, kind, project_id, coalesce(run_id, ''), coalesce(conversation_id, ''),
            coalesce(item_number, 0), coalesce(artifact_id, ''), provider_id, repository_owner, repository_name
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Priority links violate repository identity uniqueness.';
    END IF;
END $$;

CREATE UNIQUE INDEX uq_chief_of_staff_priority_links_repository_target
    ON {schema}.chief_of_staff_priority_links (
        priority_id, kind, project_id, coalesce(run_id, ''), coalesce(conversation_id, ''),
        coalesce(item_number, 0), coalesce(artifact_id, ''), provider_id, repository_owner, repository_name)
    WHERE provider_id IS NOT NULL;

CREATE UNIQUE INDEX uq_chief_of_staff_priority_links_legacy_target
    ON {schema}.chief_of_staff_priority_links (
        priority_id, kind, project_id, coalesce(run_id, ''), coalesce(conversation_id, ''),
        coalesce(item_number, 0), coalesce(artifact_id, ''))
    WHERE provider_id IS NULL;

-- Both replacements now protect the same rows. Remove only the obsolete index.
DROP INDEX {schema}.uq_chief_of_staff_priority_links_target;
