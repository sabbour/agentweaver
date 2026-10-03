# PostgreSQL transactional outbox (0.1.0)

`PostgresOutbox` manages outbox tables within one service-owned PostgreSQL schema.
Pass a shared `NpgsqlDataSource` and a lowercase, non-reserved schema identifier;
call `InitializeAsync` on startup. Initialization serializes concurrent starts and
applies the versioned, embedded migration once. A newer/unknown version fails
instead of being silently overwritten. The schema qualifier isolates table names;
it does **not** enforce database-role access control or create the service's own
domain tables.

Open a connection and transaction for a service state change and call
`EnqueueAsync(connection, transaction, message)` inside that **same transaction**.
Commit only after both state and event are written. On **any** enqueue exception,
roll back the caller's transaction; PostgreSQL errors can leave it aborted, and
continuing or committing after an error is unsafe. The caller owns the connection,
transaction, commit and rollback. Stable event IDs and schema-wide idempotency
keys make matching retries return the original event and sequence; PostgreSQL
unique constraints reject competing IDs/keys and mismatched retries raise
`OutboxConflictException`. Stream sequence numbers are assigned
transactionally and order deliveries within a stream. JSON object equality uses
PostgreSQL `jsonb` semantics; occurrence timestamps are normalized to UTC at
PostgreSQL's microsecond precision.

`ClaimAsync` leases at most one earliest undelivered event per stream per batch,
with a persisted fresh token and server-clock expiration. Workers publish then
`AcknowledgeAsync(id, token)`. Acknowledgment succeeds only for a live, current
lease; missing, stale, expired, and already acknowledged leases return `false`.
An expired lease is retried with a new token after a process restart or a
publish-before-ack failure. Delivery is **at least once**, not exactly once:
consumers must deduplicate. This library does not provide a relay daemon,
heartbeat, consumer inbox, transport, or cross-service transaction. No local
database fallback is provided.
