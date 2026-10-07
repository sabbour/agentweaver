ALTER TABLE {schema}.usage_ledger
    ALTER COLUMN request_count DROP NOT NULL;

ALTER TABLE {schema}.usage_ledger
    ADD COLUMN cache_write_tokens bigint
        CHECK (cache_write_tokens IS NULL OR cache_write_tokens >= 0);
