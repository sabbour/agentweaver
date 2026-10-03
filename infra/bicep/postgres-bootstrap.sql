-- Definition only. Requires separate approval and an Entra admin connection
-- to postgres. principal_oid is a UAMI principalId, never its clientId.
\set ON_ERROR_STOP on
SELECT pg_catalog.pgaadauth_create_principal_with_oid(
  :'runtime_role', :'principal_oid', 'service', false, false);
SELECT format('ALTER ROLE %I NOCREATEROLE NOCREATEDB NOSUPERUSER NOREPLICATION NOINHERIT', :'runtime_role') \gexec
-- A separate migration principal must exist before this step. It owns the
-- schema and applies PostgresOutbox migrations; runtime never runs migrations.
\connect :database
SELECT format('REVOKE CREATE ON SCHEMA public FROM PUBLIC') \gexec
SELECT format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database()) \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM %I', current_database(), :'runtime_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'runtime_role') \gexec
SELECT format('CREATE SCHEMA %I AUTHORIZATION %I', :'service_schema', :'migration_role') \gexec
SELECT format('REVOKE ALL ON SCHEMA %I FROM PUBLIC', :'service_schema') \gexec
SELECT format('REVOKE ALL ON SCHEMA %I FROM %I', :'service_schema', :'runtime_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', :'service_schema', :'runtime_role') \gexec
-- Apply migrations as migration_role next, then apply reviewed table grants
-- from postgres-runtime-grants.sql. No blanket/default table grants here.
