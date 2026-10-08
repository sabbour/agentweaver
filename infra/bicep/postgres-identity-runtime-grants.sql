-- Definition only. Apply after the approved Identity broker migration Job.
\set ON_ERROR_STOP on
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I FROM PUBLIC',
  'identity_broker', 'broker_users',
  'identity_broker', 'pending_authorizations',
  'identity_broker', 'secret_grant_heads',
  'identity_broker', 'secret_grant_revisions',
  'identity_broker', 'secret_grant_operations',
  'identity_broker', 'OpenIddictApplications',
  'identity_broker', 'OpenIddictAuthorizations',
  'identity_broker', 'OpenIddictScopes',
  'identity_broker', 'OpenIddictTokens') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I FROM PUBLIC',
  'identity_broker', '__ef_migrations_history') \gexec
SELECT format('REVOKE CREATE ON SCHEMA identity_broker FROM %I', :'runtime_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA identity_broker TO %I', :'runtime_role') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I FROM %I',
  'identity_broker', 'broker_users',
  'identity_broker', 'pending_authorizations',
  'identity_broker', 'secret_grant_heads',
  'identity_broker', 'secret_grant_revisions',
  'identity_broker', 'secret_grant_operations',
  'identity_broker', 'OpenIddictApplications',
  'identity_broker', 'OpenIddictAuthorizations',
  'identity_broker', 'OpenIddictScopes',
  'identity_broker', 'OpenIddictTokens',
  :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I, %I.%I TO %I',
  'identity_broker', 'broker_users',
  'identity_broker', 'pending_authorizations',
  'identity_broker', 'secret_grant_heads',
  'identity_broker', 'secret_grant_revisions',
  'identity_broker', 'secret_grant_operations',
  'identity_broker', 'OpenIddictApplications',
  'identity_broker', 'OpenIddictAuthorizations',
  'identity_broker', 'OpenIddictScopes',
  'identity_broker', 'OpenIddictTokens',
  :'runtime_role') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I FROM %I',
  'identity_broker', '__ef_migrations_history', :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT ON TABLE %I.%I TO %I',
  'identity_broker', '__ef_migrations_history', :'runtime_role') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I, %I.%I, %I.%I FROM PUBLIC',
  'identity_broker', 'runtime_grant_heads',
  'identity_broker', 'runtime_grant_revisions',
  'identity_broker', 'runtime_grant_operations',
  'identity_broker', 'runtime_grant_operation_receipts') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I, %I.%I, %I.%I FROM %I',
  'identity_broker', 'runtime_grant_heads',
  'identity_broker', 'runtime_grant_revisions',
  'identity_broker', 'runtime_grant_operations',
  'identity_broker', 'runtime_grant_operation_receipts',
  :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT, INSERT, UPDATE ON TABLE %I.%I TO %I',
  'identity_broker', 'runtime_grant_heads', :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT, INSERT ON TABLE %I.%I, %I.%I, %I.%I TO %I',
  'identity_broker', 'runtime_grant_revisions',
  'identity_broker', 'runtime_grant_operations',
  'identity_broker', 'runtime_grant_operation_receipts',
  :'runtime_role') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I FROM PUBLIC',
  'identity_broker', 'copilot_connections',
  'identity_broker', 'copilot_connection_revisions') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I FROM %I',
  'identity_broker', 'copilot_connections',
  'identity_broker', 'copilot_connection_revisions',
  :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT, INSERT, UPDATE ON TABLE %I.%I TO %I',
  'identity_broker', 'copilot_connections', :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT, INSERT ON TABLE %I.%I TO %I',
  'identity_broker', 'copilot_connection_revisions', :'runtime_role') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I, %I.%I, %I.%I FROM PUBLIC',
  'identity_broker', 'repo_app_authorization_transactions',
  'identity_broker', 'repo_app_connections',
  'identity_broker', 'repo_app_installations',
  'identity_broker', 'repo_app_repository_selections') \gexec
SELECT format(
  'REVOKE ALL ON TABLE %I.%I, %I.%I, %I.%I, %I.%I FROM %I',
  'identity_broker', 'repo_app_authorization_transactions',
  'identity_broker', 'repo_app_connections',
  'identity_broker', 'repo_app_installations',
  'identity_broker', 'repo_app_repository_selections',
  :'runtime_role') \gexec
SELECT format(
  'GRANT SELECT, INSERT, UPDATE ON TABLE %I.%I, %I.%I, %I.%I, %I.%I TO %I',
  'identity_broker', 'repo_app_authorization_transactions',
  'identity_broker', 'repo_app_connections',
  'identity_broker', 'repo_app_installations',
  'identity_broker', 'repo_app_repository_selections',
  :'runtime_role') \gexec
