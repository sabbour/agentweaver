ALTER TABLE {schema}.usage_rate_cards
    ADD COLUMN azure_retail_request_digest varchar(67);

ALTER TABLE {schema}.usage_rate_cards
    ADD CONSTRAINT ck_usage_rate_cards_azure_retail_request_digest
        CHECK (azure_retail_request_digest IS NULL OR azure_retail_request_digest ~ '^v1:[0-9a-f]{64}$');
