CREATE TABLE "{schema}".consumer_inbox_receipts (
    consumer_id text NOT NULL,
    message_id text NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (consumer_id, message_id)
);
