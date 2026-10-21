-- Spend budget exposure. Events stays the only usage ledger: these tables hold dispatch reservations
-- (exposure) and the highest Events accounting cursor each meter has observed, never spend totals.

-- One row per (dispatch, budget bucket). Admission adds the reserve_amount of every outstanding row in a
-- bucket to the committed Events total before it admits a new dispatch. A row moves to 'retired' only after
-- Events accounts for every receipt that the dispatch acknowledged. Rows are never deleted, so the table is
-- also an audit trail of what each dispatch reserved.
CREATE TABLE {schema}.spend_budget_reservations (
    -- The dispatch ID is also the reservation identity, so a retried dispatch finds its own row.
    dispatch_id uuid NOT NULL CHECK (dispatch_id <> '00000000-0000-0000-0000-000000000000'),
    -- Stable bucket identity. It excludes the budget revision and limits, so editing a budget keeps
    -- earlier exposure in the same bucket. Run and agent bucket keys also include the run ID.
    bucket_key varchar(1024) NOT NULL CHECK (length(bucket_key) > 0),
    tenant_id varchar(256) NOT NULL CHECK (length(tenant_id) > 0),
    project_id varchar(256) NOT NULL CHECK (length(project_id) > 0),
    run_id varchar(256) NOT NULL CHECK (length(run_id) > 0),
    agent_id varchar(256) NOT NULL CHECK (length(agent_id) > 0),
    budget_id varchar(128) NOT NULL CHECK (length(budget_id) > 0),
    -- Budget revision at admission time. A retry with a different revision is a reservation conflict.
    budget_revision bigint NOT NULL CHECK (budget_revision > 0),
    scope varchar(16) NOT NULL CHECK (scope IN ('project', 'run', 'agent')),
    meter_source varchar(256) NOT NULL CHECK (length(meter_source) > 0),
    unit varchar(64) NOT NULL CHECK (length(unit) > 0),
    -- Period columns are set only for project budgets. Run and agent budgets cover the whole run.
    period_id varchar(128),
    period_starts_at timestamptz,
    period_ends_at timestamptz,
    -- Full reserve held for the dispatch. Admission stores only positive reserves; a zero reserve adds no
    -- exposure, so it gets no row.
    reserve_amount numeric NOT NULL CHECK (reserve_amount > 0),
    -- Project configuration revision that supplied the budget: the pinned revision for run and agent
    -- budgets, or the current revision for project budgets.
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    -- Hash of the accepted selection that admitted the dispatch, stored as 64 hex characters (SHA-256).
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    -- 'outstanding' rows count as exposure. 'retired' rows no longer count because Events includes their spend.
    reservation_state varchar(16) NOT NULL DEFAULT 'outstanding'
        CHECK (reservation_state IN ('outstanding', 'retired')),
    -- Events accounting cursor of the evidence that proved the dispatch was fully accounted.
    retired_cursor bigint CHECK (retired_cursor >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    retired_at timestamptz,
    -- One reservation per dispatch and bucket. Retries reuse the row instead of adding exposure again.
    PRIMARY KEY (dispatch_id, bucket_key),
    -- Only project budgets have a period.
    CHECK ((scope = 'project') = (period_id IS NOT NULL)),
    -- Period ID, start, and end are all set or all NULL.
    CHECK ((period_id IS NULL) = (period_starts_at IS NULL) AND
        (period_id IS NULL) = (period_ends_at IS NULL)),
    -- A period must end after it starts.
    CHECK (period_ends_at IS NULL OR period_ends_at > period_starts_at),
    -- Retired rows record both the retiring cursor and the time. Outstanding rows record neither.
    CHECK ((reservation_state = 'retired') = (retired_cursor IS NOT NULL) AND
        (reservation_state = 'retired') = (retired_at IS NOT NULL))
);

-- Admission reads the outstanding reservations of one bucket on every dispatch. The partial index holds
-- only outstanding rows, so retired history does not slow that read down.
CREATE INDEX ix_spend_budget_reservations_outstanding
    ON {schema}.spend_budget_reservations(tenant_id, project_id, bucket_key, run_id, created_at)
    WHERE reservation_state = 'outstanding';

-- Highest Events accounting cursor that admission has used for each (tenant, project, meter). Admission
-- rejects an Events response with a lower cursor as stale, so an out-of-order response cannot reopen budget
-- capacity that newer evidence already counted.
CREATE TABLE {schema}.spend_budget_accounting_cursors (
    tenant_id varchar(256) NOT NULL CHECK (length(tenant_id) > 0),
    project_id varchar(256) NOT NULL CHECK (length(project_id) > 0),
    meter_source varchar(256) NOT NULL CHECK (length(meter_source) > 0),
    accounting_cursor bigint NOT NULL CHECK (accounting_cursor >= 0),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, project_id, meter_source)
);

-- Protects reservations from changes that would hide or rewrite exposure. Errors use SQLSTATE 55000
-- (object_not_in_prerequisite_state) so callers can tell a guard failure apart from other errors.
CREATE FUNCTION {schema}.guard_spend_budget_reservation_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Deleting a reservation would drop exposure without proof that Events accounted for it.
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Spend budget reservations cannot be deleted.' USING ERRCODE = '55000';
    END IF;
    -- The only allowed update is outstanding -> retired. Compare every column except the three retirement
    -- columns, so the dispatch, bucket, budget identity, and reserve amount can never change. A retired row
    -- cannot go back to outstanding or be retired a second time.
    IF OLD.reservation_state <> 'outstanding' OR NEW.reservation_state <> 'retired' OR
        (to_jsonb(NEW) - ARRAY['reservation_state', 'retired_cursor', 'retired_at']) IS DISTINCT FROM
        (to_jsonb(OLD) - ARRAY['reservation_state', 'retired_cursor', 'retired_at']) THEN
        RAISE EXCEPTION 'Spend budget reservations can only move from outstanding to retired.'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;

-- Trigger functions run only from their triggers, so no role gets direct EXECUTE.
REVOKE ALL ON FUNCTION {schema}.guard_spend_budget_reservation_mutation() FROM PUBLIC;

-- Inserts are not guarded; the table constraints already validate new rows.
CREATE TRIGGER spend_budget_reservations_guard
    BEFORE UPDATE OR DELETE ON {schema}.spend_budget_reservations
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_spend_budget_reservation_mutation();

-- Keeps each accounting cursor monotonic. Moving it back would let stale Events evidence pass the freshness
-- check again.
CREATE FUNCTION {schema}.guard_spend_budget_cursor_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Deleting a cursor would reset it, which is the same as moving it back to the start.
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Spend budget accounting cursors cannot be deleted.' USING ERRCODE = '55000';
    END IF;
    -- The row key (tenant, project, meter) cannot change, and the cursor can stay the same or increase only.
    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.project_id IS DISTINCT FROM OLD.project_id OR
        NEW.meter_source IS DISTINCT FROM OLD.meter_source OR NEW.accounting_cursor < OLD.accounting_cursor THEN
        RAISE EXCEPTION 'Spend budget accounting cursors can only move forward.' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;

REVOKE ALL ON FUNCTION {schema}.guard_spend_budget_cursor_mutation() FROM PUBLIC;

CREATE TRIGGER spend_budget_accounting_cursors_guard
    BEFORE UPDATE OR DELETE ON {schema}.spend_budget_accounting_cursors
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_spend_budget_cursor_mutation();

-- Row-level triggers do not fire on TRUNCATE, so a separate statement-level trigger blocks it on both tables.
CREATE FUNCTION {schema}.reject_spend_budget_truncate()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Spend budget exposure tables cannot be truncated.' USING ERRCODE = '55000';
END
$$;

REVOKE ALL ON FUNCTION {schema}.reject_spend_budget_truncate() FROM PUBLIC;

CREATE TRIGGER spend_budget_reservations_no_truncate
    BEFORE TRUNCATE ON {schema}.spend_budget_reservations
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_spend_budget_truncate();

CREATE TRIGGER spend_budget_accounting_cursors_no_truncate
    BEFORE TRUNCATE ON {schema}.spend_budget_accounting_cursors
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_spend_budget_truncate();
