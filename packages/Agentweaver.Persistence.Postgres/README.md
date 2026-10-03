# PostgreSQL transactional outbox and consumer inbox (0.2.0)

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

For an incoming at-least-once delivery, use `AdmitAsync(connection, transaction,
consumerId, messageId)` on the **same open connection and caller-owned transaction**
as the consumer's domain work and any `EnqueueAsync` call. The caller must run
domain work and enqueue only when the result is `InboxAdmission.Admitted`, then
commit; `InboxAdmission.Duplicate` means skip those effects and acknowledge the
delivery only after the transaction commits. Use a stable producer-assigned
message identity (such as the outbox event ID or documented idempotency key)
and a stable logical consumer identity across process restarts. A receipt is
unique per `(consumerId, messageId)` within the service-owned schema, not across
services/schemas. The database unique constraint makes competing transactions
wait: a committed receipt produces `Duplicate`, while rollback allows a waiting
retry to admit. Rollback also discards domain effects and outbox events in that
transaction. Do not acknowledge delivery before commit; on exception or
cancellation roll back and retry the delivery. The caller remains responsible
for transaction and transport acknowledgment policy.

Startup migration 2 adds receipts to existing version-1 schemas without
replacing outbox rows; the existing migration lock and unknown-version refusal
remain in effect. A canceled admission or missing/blank identity is rejected
without a receipt. Receipt retention/cleanup is not provided: deleting receipts
while messages may be retried would permit repeated effects.

`ClaimAsync` leases at most one earliest undelivered event per stream per batch,
with a persisted fresh token and server-clock expiration. Workers publish then
`AcknowledgeAsync(id, token)`. Acknowledgment succeeds only for a live, current
lease; missing, stale, expired, and already acknowledged leases return `false`.
An expired lease is retried with a new token after a process restart or a
publish-before-ack failure. Delivery is **at least once**, not exactly once:
the inbox prevents repeated committed local effects only when callers gate all
effects on admission and use the same transaction. External side effects and
transport acknowledgments are not atomic with that transaction. This library
does not provide a consumer daemon, relay daemon, heartbeat, transport, or
cross-service transaction. No local database fallback is provided.
