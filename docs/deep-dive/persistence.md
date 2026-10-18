# Persistence

Use this page to understand where Agentweaver stores authoritative state and
how services deliver changes without a cross-service transaction. PostgreSQL,
the run journal, Blob objects, and workspace volumes serve different purposes.

## Understand the storage boundaries

PostgreSQL stores service-owned state. The shared persistence package provides
local outbox and inbox primitives; each service owns its domain schema and
transaction boundaries.

An **outbox** records delivery intent in the same transaction as a local
change. An **inbox** records that a consumer admitted a message with its local
effects. These mechanisms support reliable retries, but they do not create a
transaction across services.

| Data | Owner |
| --- | --- |
| Service domain records | The owning service's PostgreSQL schema |
| Ordered run journal and session references | Events & Sessions |
| Large content bytes | Object Store |
| Agent-visible repository files | Environment workspace volume |
| Microsoft Agent Framework (MAF) execution checkpoints | Orchestrator |
| Knowledge records | The selected Memory provider |

Telemetry does not reconstruct these records.

## Find the implementation

- `packages/Agentweaver.Persistence.Postgres/` contains `PostgresOutbox.cs`,
  outbox contracts, relay logic, inbox admission, and shared migrations.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/SessionsJournal.cs`
  owns journal ordering; `EventsAndSessionsMigrator.cs` verifies its schema.
- `services/projects-config/Agentweaver.Projects.Config/ProjectsConfigMigrator.cs`
  owns EF migrations and runtime database-role checks.
- `services/knowledge/Agentweaver.Knowledge/KnowledgeMigrator.cs` applies its
  embedded SQL migrations.

## Follow an outbox message

To use the shared relay, the producer must have a local transaction and the
host must invoke the relay with an `IOutboxPublisher`. The library does not
start a background service.

1. The producer writes domain state and calls `EnqueueAsync` on the same open
   connection and transaction. A rollback removes both.
2. The host calls `OutboxRelay.RelayOnceAsync`.
3. `ClaimAsync` leases the earliest undelivered event from each eligible stream.
   PostgreSQL row locks and `SKIP LOCKED` let workers claim different work.
4. The relay calls the injected publisher.
5. After confirmed publication, the relay acknowledges the event with the
   current, unexpired lease token.
6. The consumer starts its own transaction and calls `AdmitAsync` for the
   consumer/message identity.
7. The consumer applies effects only for a new admission, commits them with the
   inbox receipt, and then acknowledges transport delivery.

`ClaimOneAsync` applies the same stream-head rule to one selected event. It
returns no lease if the event is absent, delivered, leased, or behind another
undelivered event. Orchestrator uses this path for synchronous owner-message
admission.

## Understand delivery guarantees

Delivery is **at least once**. Publication may complete before the publisher
receives confirmation, or a process may stop after publishing but before it
acknowledges its lease. Either case can cause redelivery.

The inbox prevents duplicate committed local effects only when the consumer
applies those effects for `InboxAdmission.Admitted` and commits them with the
receipt. Effects outside that transaction do not gain exactly-once behavior.

The outbox assigns sequence numbers under the stream-row lock. Repeating an
identical event returns its saved identity and sequence. Reusing an event ID or
idempotency key for different data raises `OutboxConflictException`. An exact
duplicate does not consume a new committed stream position.

## Store journal content and objects

Events & Sessions owns journal order, session identity, provider bindings, and
object-reference retention. Its `events_sessions` schema combines shared
outbox/inbox tables with Events domain tables; the persistence package does not
define a session journal.

Large content can use typed opaque object references. The PostgreSQL reference
commit and Blob upload are separate operations; they do not share an atomic
transaction. The object bytes are written before a specialized material
writer commits their reference. An unreferenced object is not a committed
journal event.

## Run migrations explicitly

Before migrating, use the owner's explicit maintenance path and database
identity. Ordinary service startup verifies the schema but does not have
migration authority.

`PostgresOutbox.InitializeAsync` validates the schema name, takes a
schema-scoped advisory lock, applies embedded migrations in order, and records
versions in a transaction. Unknown or inconsistent shared versions stop
initialization.

Owners add their own domain tables and migration locks. Events & Sessions and
Knowledge use embedded SQL; Projects & Config uses EF migrations and its own
history table. Run migration as an explicit maintenance operation. Ordinary
service startup verifies the schema, migration inventory, and owner-specific
runtime privileges. A runtime identity does not receive migration authority
just because its service reads the tables.

## Recover from delivery failures

A failed publication stays unacknowledged and can be reclaimed after its lease
expires. Failure in one claimed stream does not stop other streams in the
batch. Cancellation stops the relay before acknowledgment, even if publication
is still completing externally.

Acknowledgment checks lease expiry after acquiring the row lock. A stale or
expired token cannot settle an event. If an inbox transaction rolls back, the
message remains eligible for a fresh admission.

## Related content

- [PostgreSQL and object contracts](../architecture/persistence-objects.md)
- [Events and sessions](./events-sessions.md)
- [Knowledge and memory](./knowledge-memory.md)
- [Service ownership design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
