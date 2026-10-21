-- Spend budget exposure. Events stays the only usage ledger: these tables hold dispatch reservations
-- (exposure) and the highest Events accounting cursor each meter has observed, never spend totals.
CREATE TABLE {schema}.spend_budget_reservations (
    dispatch_id uuid NOT NULL CHECK (dispatch_id <> '00000000-0000-0000-0000-000000000000'),
    bucket_key varchar(1024) NOT NULL CHECK (length(bucket_key) > 0),
    tenant_id varchar(256) NOT NULL CHECK (length(tenant_id) > 0),
    project_id varchar(256) NOT NULL CHECK (length(project_id) > 0),
    run_id varchar(256) NOT NULL CHECK (length(run_id) > 0),
    agent_id varchar(256) NOT NULL CHECK (length(agent_id) > 0),
    budget_id varchar(128) NOT NULL CHECK (length(budget_id) > 0),
    budget_revision bigint NOT NULL CHECK (budget_revision > 0),
    scope varchar(16) NOT NULL CHECK (scope IN ('project', 'run', 'agent')),
    meter_source varchar(256) NOT NULL CHECK (length(meter_source) > 0),
    unit varchar(64) NOT NULL CHECK (length(unit) > 0),
    period_id varchar(128),
    period_starts_at timestamptz,
    period_ends_at timestamptz,
    reserve_amount numeric NOT NULL CHECK (reserve_amount > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    reservation_state varchar(16) NOT NULL DEFAULT 'outstanding'
        CHECK (reservation_state IN ('outstanding', 'retired')),
    retired_cursor bigint CHECK (retired_cursor >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    retired_at timestamptz,
    PRIMARY KEY (dispatch_id, bucket_key),
    CHECK ((scope = 'project') = (period_id IS NOT NULL)),
    CHECK ((period_id IS NULL) = (period_starts_at IS NULL) AND
        (period_id IS NULL) = (period_ends_at IS NULL)),
    CHECK (period_ends_at IS NULL OR period_ends_at > period_starts_at),
    CHECK ((reservation_state = 'retired') = (retired_cursor IS NOT NULL) AND
        (reservation_state = 'retired') = (retired_at IS NOT NULL))
);

CREATE INDEX ix_spend_budget_reservations_outstanding
    ON {schema}.spend_budget_reservations(tenant_id, project_id, bucket_key, run_id, created_at)
    WHERE reservation_state = 'outstanding';

CREATE TABLE {schema}.spend_budget_accounting_cursors (
    tenant_id varchar(256) NOT NULL CHECK (length(tenant_id) > 0),
    project_id varchar(256) NOT NULL CHECK (length(project_id) > 0),
    meter_source varchar(256) NOT NULL CHECK (length(meter_source) > 0),
    accounting_cursor bigint NOT NULL CHECK (accounting_cursor >= 0),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, project_id, meter_source)
);

CREATE FUNCTION {schema}.guard_spend_budget_reservation_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Spend budget reservations cannot be deleted.' USING ERRCODE = '55000';
    END IF;
    IF OLD.reservation_state <> 'outstanding' OR NEW.reservation_state <> 'retired' OR
        (to_jsonb(NEW) - ARRAY['reservation_state', 'retired_cursor', 'retired_at']) IS DISTINCT FROM
        (to_jsonb(OLD) - ARRAY['reservation_state', 'retired_cursor', 'retired_at']) THEN
        RAISE EXCEPTION 'Spend budget reservations can only move from outstanding to retired.'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;

REVOKE ALL ON FUNCTION {schema}.guard_spend_budget_reservation_mutation() FROM PUBLIC;

CREATE TRIGGER spend_budget_reservations_guard
    BEFORE UPDATE OR DELETE ON {schema}.spend_budget_reservations
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_spend_budget_reservation_mutation();

CREATE FUNCTION {schema}.guard_spend_budget_cursor_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Spend budget accounting cursors cannot be deleted.' USING ERRCODE = '55000';
    END IF;
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
