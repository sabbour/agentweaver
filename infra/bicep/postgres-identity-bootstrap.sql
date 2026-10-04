-- Definition only. Requires separate approval and an Entra admin connection
-- to postgres. Object IDs are UAMI principalIds, never workload clientIds.
\set ON_ERROR_STOP on
SELECT pg_catalog.pgaadauth_create_principal_with_oid(
  :'runtime_role', :'runtime_principal_oid', 'service', false, false);
SELECT pg_catalog.pgaadauth_create_principal_with_oid(
  :'migration_role', :'migration_principal_oid', 'service', false, false);
SELECT format(
  'ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT',
  :'runtime_role') \gexec
SELECT format(
  'ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT',
  :'migration_role') \gexec

\connect :database
SELECT format('REVOKE CREATE ON SCHEMA public FROM PUBLIC') \gexec
SELECT format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database()) \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM %I', current_database(), :'runtime_role') \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM %I', current_database(), :'migration_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'runtime_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'migration_role') \gexec
SELECT format('CREATE SCHEMA identity_broker AUTHORIZATION %I', :'migration_role') \gexec
SELECT format('REVOKE ALL ON SCHEMA identity_broker FROM PUBLIC') \gexec
SELECT format('REVOKE ALL ON SCHEMA identity_broker FROM %I', :'runtime_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA identity_broker TO %I', :'runtime_role') \gexec
