-- Definition only. A separate approval covers these post-migration grants.
-- Connect as the migration/schema owner to the dedicated service database.
\set ON_ERROR_STOP on
SELECT format('GRANT SELECT, INSERT, UPDATE ON TABLE %I.outbox_streams, %I.outbox_events TO %I',
  :'service_schema', :'service_schema', :'runtime_role') \gexec
SELECT format('GRANT SELECT, INSERT ON TABLE %I.consumer_inbox_receipts TO %I',
  :'service_schema', :'runtime_role') \gexec
SELECT format('REVOKE ALL ON TABLE %I.outbox_schema_migrations FROM %I',
  :'service_schema', :'runtime_role') \gexec
-- Current persistence migrations use no SQL sequences. Additional domain
-- tables and sequence USAGE require reviewed service-owned grants in #1784.
-- Apply only in the Orchestrator database after migration 023, with separate approval.
SELECT format('GRANT SELECT, INSERT, UPDATE ON TABLE %I.project_run_requests TO %I',
  :'service_schema', :'runtime_role')
WHERE to_regclass(format('%I.project_run_requests', :'service_schema')) IS NOT NULL \gexec
SELECT format('GRANT SELECT, INSERT ON TABLE %I.project_run_cancellations TO %I',
  :'service_schema', :'runtime_role')
WHERE to_regclass(format('%I.project_run_cancellations', :'service_schema')) IS NOT NULL \gexec
