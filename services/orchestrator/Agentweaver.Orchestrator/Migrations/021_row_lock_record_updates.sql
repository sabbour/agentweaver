CREATE FUNCTION {schema}.reject_row_lock_record_update()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Coordinator decision and MAF checkpoint records cannot be updated.'
        USING ERRCODE = '55000';
END
$$;

REVOKE ALL ON FUNCTION {schema}.reject_row_lock_record_update() FROM PUBLIC;

CREATE TRIGGER coordinator_decisions_no_update
    BEFORE UPDATE ON {schema}.coordinator_decisions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();

CREATE TRIGGER maf_workflow_checkpoints_no_update
    BEFORE UPDATE ON {schema}.maf_workflow_checkpoints
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();
