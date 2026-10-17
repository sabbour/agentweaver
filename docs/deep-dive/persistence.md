# Persistence

## The mental model

PostgreSQL stores authoritative service state. The shared persistence package supplies local outbox and inbox primitives; each service owns its domain schema and transaction boundaries.

An outbox connects a committed local change to eventual delivery. An inbox connects message admission to committed consumer effects. Neither creates a transaction across services.

The run journal is a separate Events & Sessions domain store. Blob objects hold large bytes. Workspace volumes hold agent-visible files. These stores solve different problems.

## Where this lives

- `packages/Agentweaver.Persistence.Postgres/PostgresOutbox.cs` — schema initialization, enqueue, claim, inbox admission, and acknowledgment.
- `packages/Agentweaver.Persistence.Postgres/OutboxContracts.cs` — event, delivery, and publisher contracts.
- `packages/Agentweaver.Persistence.Postgres/OutboxRelay.cs` — one caller-driven publication batch.
- `packages/Agentweaver.Persistence.Postgres/InboxAdmission.cs` — admitted/duplicate result.
- `packages/Agentweaver.Persistence.Postgres/Migrations/` — embedded shared schema changes.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/SessionsJournal.cs` — ordered run journal.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsMigrator.cs` — owner migration and startup verification.
- `services/projects-config/Agentweaver.Projects.Config/ProjectsConfigMigrator.cs` — EF migration and runtime-role boundary.
- `services/knowledge/Agentweaver.Knowledge/KnowledgeMigrator.cs` — embedded SQL owner migrations.

## Core concepts and invariants

### Local state and delivery intent commit together

`EnqueueAsync` accepts the caller's open connection and transaction. The caller writes domain state and the outbox event in that same transaction. Rolling back also removes the delivery intent.

The outbox assigns sequence numbers under the stream-row lock. Repeating an identical event returns its stored identity and sequence. Reusing an event ID or idempotency key for different data throws `OutboxConflictException`. Duplicate retry does not consume a committed stream position.

### Delivery is at least once

Publication can complete before the publisher receives confirmation. A process can also stop after publishing but before acknowledging its lease. Both cases permit redelivery.

The inbox prevents duplicate committed local effects only when the consumer applies effects for `InboxAdmission.Admitted` and commits them with the receipt. Effects outside that transaction do not gain exactly-once behavior.

## Producer, relay, and consumer flow

1. The producer commits its domain write and `EnqueueAsync`.
2. The host calls `OutboxRelay.RelayOnceAsync`.
3. `ClaimAsync` leases the earliest undelivered event from each eligible stream. PostgreSQL row locks and `SKIP LOCKED` let workers claim disjoint work.
4. The relay calls the injected `IOutboxPublisher`.
5. A confirmed publication is acknowledged using the current, unexpired lease token.
6. The consumer starts its own transaction and calls `AdmitAsync` for the consumer/message identity.
7. It applies effects only for a new admission, commits, then acknowledges transport delivery.

`ClaimOneAsync` provides the same stream-head rule for one selected event. It returns no lease when that event is absent, already delivered, leased, or behind another undelivered event. The Orchestrator uses this targeted path for synchronous owner-message admission.

The library starts no daemon or background loop. Hosts choose when to call the relay and supply the actual transport.

## Journal and object boundaries

Events & Sessions owns journal ordering, session identity, provider bindings, and object-reference retention. Its `events_sessions` schema composes the shared outbox/inbox tables with domain tables. The persistence package itself defines no session journal.

Large content can be represented by typed opaque object references. A PostgreSQL reference commit and a Blob upload are separate operations; they do not share an atomic transaction. A stored object without a committed reference is not a committed journal event.

MAF checkpoints remain Orchestrator state. Knowledge records belong to the selected Memory provider. Azure Files workspace data belongs to the Environment volume lifecycle. None is reconstructed from telemetry.

## Migration approach

`PostgresOutbox.InitializeAsync` validates a non-system schema name, takes a schema-scoped advisory lock, applies embedded migrations in order, and records versions in a transaction. Unknown or inconsistent shared versions stop initialization.

Owners extend this boundary rather than sharing domain tables. Events & Sessions and Knowledge apply embedded SQL under their own migration locks. Projects & Config uses EF migrations with its own history table and advisory-lock coordination.

Migration execution is an explicit maintenance operation. Ordinary service startup verifies the required schema, migration inventory, and owner-specific runtime privileges. Runtime identity does not receive migration authority merely because the service needs to read its tables.

## Failure and recovery

A failed publication remains unacknowledged and can be reclaimed after lease expiry. Failure in one claimed stream does not stop another stream in the batch. Cancellation stops the relay before acknowledgment, including when publication is still finishing externally.

Acknowledgment checks expiry after acquiring the row lock. A stale or expired token cannot settle the event. An inbox transaction that rolls back leaves the message eligible for a fresh admission.

## Related pages

- [PostgreSQL and object contracts](../architecture/persistence-objects.md)
- [Events and sessions](./events-sessions.md)
- [Knowledge and memory](./knowledge-memory.md)
- [Service ownership design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
