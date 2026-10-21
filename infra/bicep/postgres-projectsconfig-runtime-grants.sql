-- Definition only. Apply after Projects & Config migrations, as the schema owner.
-- A separate approval covers every live grant in this file.
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
