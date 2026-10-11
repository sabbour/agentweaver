CREATE FUNCTION {schema}.reject_row_lock_record_update()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Session identity, provider binding, and fork lineage records cannot be updated.'
        USING ERRCODE = '55000';
END
$$;

REVOKE ALL ON FUNCTION {schema}.reject_row_lock_record_update() FROM PUBLIC;

CREATE TRIGGER sessions_no_update
    BEFORE UPDATE ON {schema}.sessions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();

CREATE TRIGGER session_provider_bindings_no_update
    BEFORE UPDATE ON {schema}.session_provider_bindings
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();

CREATE TRIGGER session_fork_lineage_no_update
    BEFORE UPDATE ON {schema}.session_fork_lineage
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();
