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

For a caller-driven batch, implement `IOutboxPublisher.PublishAsync` with the
transport's publish operation and construct `new OutboxRelay(outbox, publisher)`.
Call `RelayOnceAsync(workerId, batchSize, leaseDuration, cancellationToken)` at
the cadence owned by your host. It claims a bounded batch, publishes each claimed
event, and only then acknowledges it using the claim's lease token. It returns
one `RelayOutcome` per attempted delivery: `Acknowledged` means publish and
fenced acknowledgment succeeded; `AcknowledgmentFenced` means publish succeeded
but the lease expired, was replaced, or was already settled before acknowledgment;
`PublishFailed` carries a non-relay-cancellation publisher exception and leaves
the event unacknowledged. A failure on one stream does not prevent attempts for
other streams already claimed in that batch. Cancellation before or during
publish, or after publish but before acknowledgment, never acknowledges an
unconfirmed event; cancellation propagates rather than returning a partial
outcome list, although earlier items in the batch may have completed.

This bounded primitive is **not** a background loop, daemon, heartbeat, broker
provisioning, or an exactly-once guarantee. The relay opens no separate network
or database transaction of its own: its existing `ClaimAsync` and
`AcknowledgeAsync` calls each open their own connection as before. Host-side
scheduling, transport implementation, and consumer transaction/receipt handling
remain caller responsibilities.
