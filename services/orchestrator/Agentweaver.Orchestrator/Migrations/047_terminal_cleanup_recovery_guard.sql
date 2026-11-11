CREATE FUNCTION {schema}.guard_terminal_cleanup_recovery()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF (NEW.execution_state IS DISTINCT FROM OLD.execution_state
        OR NEW.execution_fence IS DISTINCT FROM OLD.execution_fence)
       AND (OLD.execution_state IN ('completed', 'cancelled')
            OR (OLD.execution_state IN ('idle', 'active', 'blocked')
                AND (NEW.execution_state IS DISTINCT FROM 'completed'
                     OR NEW.execution_fence IS DISTINCT FROM OLD.execution_fence)
                AND (OLD.completed_at IS NOT NULL OR EXISTS (
                    SELECT 1
                    FROM {schema}.coordination_sessions AS root
                    WHERE root.project_id = OLD.project_id
                      AND root.run_id = OLD.run_id
                      AND root.parent_session_id IS NULL
                      AND root.lifecycle_state = 'completed')))) THEN
        RAISE EXCEPTION 'run_terminal_state_final' USING ERRCODE = '55000';
    END IF;
    IF OLD.execution_state IN ('failed', 'indeterminate')
       AND (NEW.execution_state IS DISTINCT FROM OLD.execution_state
            OR NEW.execution_fence IS DISTINCT FROM OLD.execution_fence)
       AND EXISTS (
           SELECT 1
           FROM {schema}.project_run_cancellations AS cancellation
           WHERE cancellation.tenant_id = OLD.tenant_id
             AND cancellation.project_id = OLD.project_id
             AND cancellation.run_id = OLD.run_id
             AND cancellation.result -> 'terminalCleanup' ->> 'state' = OLD.execution_state
             AND cancellation.result -> 'terminalCleanup' ->> 'fence' = OLD.execution_fence::text
       ) THEN
        RAISE EXCEPTION 'runtime_start_cleanup_recovery_blocked' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER accepted_run_terminal_cleanup_guard
    BEFORE UPDATE OF execution_state, execution_fence ON {schema}.accepted_runs
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_terminal_cleanup_recovery();

CREATE OR REPLACE FUNCTION {schema}.close_workflow_generation_jobs_on_cancel()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.result ->> 'state' = 'cancelled'
       AND COALESCE(NEW.result ->> 'alreadyTerminal', 'false') = 'false' THEN
        UPDATE {schema}.workflow_generation_jobs
        SET status = 'cancelled',
            execution_phase = CASE
                WHEN execution_phase = 'send-admitted' THEN execution_phase
                ELSE 'quiesced'
            END,
            failure_code = 'workflow_generation_cancelled',
            failure_message = 'The run was cancelled.',
            updated_at = clock_timestamp()
        WHERE project_id = NEW.project_id AND run_id = NEW.run_id
          AND (status IN ('queued', 'running') OR
               (status = 'failed' AND execution_phase = 'send-admitted'));
    END IF;
    RETURN NEW;
END;
$$;
