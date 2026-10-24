-- Standalone definition of the Projects & Config runtime grants. The reviewed P1 plan
-- holds the same grants, and the Projects & Config grants step applies them. Use this
-- file only for a database whose grants are managed by hand. Apply it after Projects &
-- Config migrations, as the schema owner.
\set ON_ERROR_STOP on
SELECT format(
  'GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean) TO %I',
  :'runtime_role') \gexec

-- Project role-assignment API. The runtime service checks for a current TenantAdmin
-- and writes an authority_audit row in the same transaction as each change.
-- Runtime can add assignments and mark them revoked, but cannot change who, what,
-- or where an assignment applies, cannot delete rows, and cannot write memberships.
SELECT format(
  'GRANT INSERT ON TABLE projects_config.project_role_assignments TO %I',
  :'runtime_role') \gexec
SELECT format(
  'GRANT UPDATE (state, revision, revoked_by, revoked_at) ON TABLE projects_config.project_role_assignments TO %I',
  :'runtime_role') \gexec
SELECT format(
  'GRANT INSERT ON TABLE projects_config.authority_audit TO %I',
  :'runtime_role') \gexec

-- AgentHost publishes only reasoning levels verified from its live SDK catalog.
-- The owner scopes each row to the current actor, project, connection, and binding revision.
SELECT format(
  'GRANT SELECT, INSERT, UPDATE ON TABLE projects_config.hosted_model_reasoning_catalog TO %I',
  :'runtime_role') \gexec
