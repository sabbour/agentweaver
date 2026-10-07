CREATE TABLE IF NOT EXISTS {schema}.usage_rate_cards (
    card_id varchar(256) NOT NULL,
    version varchar(128) NOT NULL,
    meter_source varchar(256) NOT NULL,
    unit varchar(128) NOT NULL,
    nano_units_per_unit numeric NOT NULL CHECK (nano_units_per_unit >= 0),
    model_multipliers jsonb NOT NULL,
    canonical_input text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (card_id, version),
    UNIQUE (card_id, version, meter_source, unit)
);

CREATE TABLE IF NOT EXISTS {schema}.usage_ledger (
    contract_version smallint NOT NULL CHECK (contract_version = 1),
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    event_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL,
    agent_id varchar(256) NOT NULL,
    model_reference varchar(512) NOT NULL,
    model_id varchar(512) NOT NULL,
    meter_source varchar(256) NOT NULL,
    selection_revision varchar(256) NOT NULL,
    input_tokens bigint CHECK (input_tokens IS NULL OR input_tokens >= 0),
    output_tokens bigint CHECK (output_tokens IS NULL OR output_tokens >= 0),
    cached_tokens bigint CHECK (cached_tokens IS NULL OR cached_tokens >= 0),
    reasoning_tokens bigint CHECK (reasoning_tokens IS NULL OR reasoning_tokens >= 0),
    request_count bigint NOT NULL CHECK (request_count >= 0),
    provider_units numeric CHECK (provider_units IS NULL OR provider_units >= 0),
    provider_unit varchar(128),
    duration_milliseconds numeric
        CHECK (duration_milliseconds IS NULL OR duration_milliseconds >= 0),
    price_disposition varchar(16) NOT NULL
        CHECK (price_disposition IN ('Estimate', 'Reconciled', 'Unpriced')),
    price_amount numeric CHECK (price_amount IS NULL OR price_amount >= 0),
    price_unit varchar(128),
    unpriced_reason varchar(1024),
    rate_card_id varchar(256),
    rate_card_version varchar(128),
    rate_card_unit varchar(128),
    cost_binding jsonb,
    canonical_input text NOT NULL,
    canonical_input_hash varchar(64) NOT NULL
        CHECK (canonical_input_hash ~ '^[0-9a-f]{64}$'),
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, project_id, run_id, event_id),
    UNIQUE (event_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id),
    FOREIGN KEY (rate_card_id, rate_card_version, meter_source, rate_card_unit)
        REFERENCES {schema}.usage_rate_cards (card_id, version, meter_source, unit),
    CHECK ((rate_card_id IS NULL) = (rate_card_version IS NULL)),
    CHECK ((rate_card_id IS NULL) = (rate_card_unit IS NULL)),
    CHECK ((rate_card_id IS NULL) = (cost_binding IS NULL)),
    CHECK (cost_binding IS NULL OR (
        cost_binding ->> 'meterSource' IS NOT NULL
        AND cost_binding -> 'rateCard' ->> 'id' IS NOT NULL
        AND cost_binding -> 'rateCard' ->> 'version' IS NOT NULL
        AND cost_binding -> 'rateCard' ->> 'unit' IS NOT NULL
        AND cost_binding ->> 'meterSource' = meter_source
        AND cost_binding -> 'rateCard' ->> 'id' = rate_card_id
        AND cost_binding -> 'rateCard' ->> 'version' = rate_card_version
        AND cost_binding -> 'rateCard' ->> 'unit' = rate_card_unit
    )),
    CHECK (price_unit IS NULL OR price_unit = rate_card_unit),
    CHECK (
        (price_disposition = 'Unpriced' AND price_amount IS NULL AND unpriced_reason IS NOT NULL)
        OR
        (price_disposition IN ('Estimate', 'Reconciled') AND price_amount IS NOT NULL
            AND price_unit IS NOT NULL AND unpriced_reason IS NULL
            AND rate_card_id IS NOT NULL AND rate_card_version IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_usage_ledger_run_agent
    ON {schema}.usage_ledger (tenant_id, project_id, run_id, agent_id, event_id);

CREATE OR REPLACE FUNCTION {schema}.reject_usage_history_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'usage accounting history is append-only';
END;
$$;

CREATE TRIGGER usage_ledger_append_only
    BEFORE UPDATE OR DELETE ON {schema}.usage_ledger
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_usage_history_mutation();

CREATE TRIGGER usage_ledger_no_truncate
    BEFORE TRUNCATE ON {schema}.usage_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_usage_history_mutation();

CREATE TRIGGER usage_rate_cards_immutable
    BEFORE UPDATE OR DELETE ON {schema}.usage_rate_cards
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_usage_history_mutation();

CREATE TRIGGER usage_rate_cards_no_truncate
    BEFORE TRUNCATE ON {schema}.usage_rate_cards
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_usage_history_mutation();
