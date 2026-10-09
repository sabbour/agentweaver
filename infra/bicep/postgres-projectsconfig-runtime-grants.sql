-- Definition only. Apply after Projects & Config migrations, as the schema owner.
\set ON_ERROR_STOP on
SELECT format(
  'GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean) TO %I',
  :'runtime_role') \gexec
