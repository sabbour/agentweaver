# Events and sessions

Use this page to understand what Agentweaver records as durable run history
and how that history differs from current execution state. Orchestrator owns
the session tree, gates, execution fence, and turn state. Events & Sessions
owns ordered journal entries, material references, addressed-message delivery,
and usage accounting.

Replaying an event displays committed history. It does not approve a gate,
deliver a tool call again, or restart a model.

## Find the implementation

| Source | Responsibility |
| --- | --- |
| `SessionsEndpoints.cs` and `SessionsJournal.cs` | Session creation, append, replay, subscriptions, cursors, and journal storage |
| `SessionsProviderBindingService.cs` | Immutable Sessions provider selection and verification |
| `PostgresAddressedMessageStore.cs` and `AddressedMessageEndpoints.cs` | Thread ordering and fenced message delivery |
| `CoordinationOwnerClient.cs` | Current Orchestrator admission and session-binding checks |
| `SessionMaterialStore.cs` and `SessionMaterialEndpoints.cs` | Producer-owned material writes and reads |
| `PublicSessionEventProjection.cs` | Removes private configuration and remote-tool result details |
| `AcceptedEffectFacts.cs` | Separate project-scoped accepted-effect facts |
| `Usage/` and `Cost/` | Receipt admission, usage ledger, and cost-provider binding |

These files are under `services/events-and-sessions/Agentweaver.EventsAndSessions/`.
Shared journal and message contracts live in `packages/Agentweaver.Abstractions/`;
outbox and inbox primitives live in `packages/Agentweaver.Persistence.Postgres/`.

## Understand journal identity and provider pins

A session identity contains project, run, and session IDs. Sessions in the same
project/run share a monotonically advancing run position. Session replay filters
that order; run replay includes events from all sessions.

Event IDs identify immutable content within a run. Reusing the same ID with
canonical identical input returns the saved acknowledgment. Reusing it with
different content conflicts.

Creating a session saves the run's Sessions provider binding in the same
transaction. The binding records provider and adapter versions, options
revision, resource generation, and negotiated capabilities. Later operations
verify that exact binding. Changing the platform default does not move an
existing run to a new provider.

The journal stores typed references and retention metadata; Object Store holds
the bytes. A material write persists bytes before the journal transaction
commits its reference. Unreferenced bytes do not constitute an acknowledged
session event.

## Append and replay events

Before appending, the caller needs an existing session, a valid event identity,
and any producer-specific authority required for the material.

`PostgresSessionsJournal.AppendCoreAsync` performs the durable append:

1. Validate event version, payload, identity, and references.
2. Open the service-owned database transaction and confirm the session exists.
3. Admit the scoped event ID through the consumer inbox.
4. For an exact duplicate, compare canonical content and return the existing
   event after required producer checks.
5. Persist producer bytes when a specialized writer supplies them.
6. Advance the run position and insert the event, references, and outbox record.
7. Revalidate specialized owner authority and commit.

Only committed rows appear in replay. A page contains at most 200 events.
Cursors encode stream scope and position; a session cursor cannot stand in for
a run-wide cursor or another session's cursor.

The native subscription implementation polls journal pages. A subscription
allows at most 10,000 events and 300 seconds. Reconnect with the returned opaque
cursor. Gateway's public server-sent events (SSE) path separately reads
committed run pages, projects them, and rechecks Projects read authority before
each emitted event.
Neither replay path relies on a process-local event channel for durable history.

## Deliver addressed messages

An addressed message has a sender, recipient, thread, delivery mode, execution
binding, and expiry. Its delivery record is separate from workflow decisions.
Before delivery, Events checks the Orchestrator-owned route and outbox proof.

1. **Admit.** Events stores the message, journal evidence, and outbox entry
   together.
2. **Claim.** At a presenting turn boundary, Events checks the recipient's
   current owner binding and leases an eligible message.
3. **Present.** A current claim marks the message delivered to its addressed
   session.
4. **Acknowledge.** The recipient acknowledges with the claim owner and fence.

Claims use increasing fences. An expired lease allows another claim but does
not preserve the old claimant's access. `Immediate` messages take priority
over queued messages, while the per-thread predicate preserves ordering.
Acknowledgment records receipt only. The Orchestrator's separate gate operation
decides approval. Message acceptance does not prove AgentHost executed a turn.

## Read material and fork a session

The typed material interface accepts execution-bound content, not a
caller-chosen Blob key or URL. Writes verify current Projects authority and
Orchestrator runtime registration. Stored metadata binds content length, digest,
SDK/runtime versions, and model selection.

Reads start from a committed journal event and verify the recorded bytes.
Historical turn reads require current run-read authority and session membership.
SDK-cache reads keep the stricter run-selection boundary. Cache reuse also
requires compatible SDK and model bindings.

Ordinary event responses hide private runtime configuration and reviewed
remote-tool result material. Private remote-tool result routes use their
configured mutual-TLS owner boundary, not the generic material route.

To fork a session, you need a committed source cursor and current Orchestrator
fork admission. Events verifies that admission, preserves source history, and
creates target session lineage under the saved provider binding. A cursor marks
a history boundary; it does not grant permission to repeat the source session's
effects.

## Record usage and project facts

Usage writes consume immutable Orchestrator source receipts by reference. The
consumer checks the source, pins Cost binding and rate-card identity,
deduplicates receipts, and commits accounting with its acknowledgment. A
client-supplied token count is not an authenticated SDK receipt.

The usage ledger is separate from telemetry and journal replay. Missing pricing
stays explicit; it does not become a zero charge. Run admission and
reconciliation read priced totals through this boundary.

Project accepted-effect facts use a separate stream. A project-level Knowledge
change does not need a fabricated run or session identity.

## Diagnose journal failures

- Invalid versions, malformed cursors, and changed immutable events are rejected.
- Missing or changed pinned providers block access instead of selecting a
  replacement.
- Lost message claims and changed execution fences reject presentation or
  acknowledgment.
- Failed producer authority or integrity checks prevent material
  acknowledgment or disclosure.
- Startup verifies the expected schema. Migration uses a separate `--migrate`
  path; serving traffic does not silently initialize the schema.

## Related content

- [Orchestration](./orchestration.md) explains session and turn ownership.
- [Agent runtime](./agent-runtime.md) explains material and SDK receipt producers.
- [Persistence](./persistence.md) explains outbox and Object Store boundaries.
- [Events & Sessions architecture](../architecture/events-sessions.md) and
  [contracts and endpoints](../reference/contracts.md) record detailed contracts.
