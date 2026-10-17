# Events and sessions

## The mental model

Events & Sessions records the durable story of a run. Orchestrator owns what the
run is doing now: its session tree, gates, execution fence, and turn state.
Events owns ordered journal entries, session material references, addressed-message
delivery, and usage accounting.

Do not treat every event as a command. Replaying an event displays committed
history. It does not approve a gate, deliver a tool call again, or restart a model.

## Where this lives

- `services/events-and-sessions/Agentweaver.EventsAndSessions/Program.cs` —
  Provider composition, authentication, schema verification, and optional material
  and usage features.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/SessionsEndpoints.cs` —
  Session creation, append, replay, subscriptions, and explicit-event forks.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/SessionsJournal.cs` —
  PostgreSQL journal, run positions, deduplication, cursors, and reference metadata.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/SessionsProviderBindingService.cs` —
  Immutable Sessions provider selection and verification.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/PostgresAddressedMessageStore.cs`
  and `AddressedMessageEndpoints.cs` — Thread ordering and fenced delivery.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/CoordinationOwnerClient.cs` —
  Current Orchestrator admission and session-binding checks.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/SessionMaterialStore.cs`
  and `SessionMaterialEndpoints.cs` — Producer-owned material storage and reads.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/PublicSessionEventProjection.cs` —
  Removal of private configuration and remote-tool result details.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/AcceptedEffectFacts.cs` —
  Separate project-scoped accepted-effect facts.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/Usage/` — SDK receipt
  admission and the append-only usage ledger.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/Cost/` — Cost
  provider implementations and binding.
- `packages/Agentweaver.Abstractions/SessionsContracts.cs` and
  `AddressedMessageContracts.cs` — Shared journal and message contracts.
- `packages/Agentweaver.Persistence.Postgres/` — Outbox and inbox primitives.

## Core concepts and invariants

### One run has one ordered journal

A session identity contains project, run, and session IDs. All sessions in the
same project/run share a monotonically advancing run position. Session replay
filters that order; run replay includes events across sessions.

Event IDs identify immutable content within the run. The same ID and canonical
input return the stored acknowledgment. Reusing the ID with different content
conflicts.

### The provider pin outlives a default change

Creating a session saves the run's Sessions provider binding in the same
transaction. The binding records provider and adapter versions, options revision,
resource generation, and negotiated capabilities.

Subsequent operations verify that exact binding. A changed platform default does
not move an existing run to another provider.

### References are not bytes

The journal stores typed references and retention metadata. Object Store holds
the associated bytes. A material write persists bytes before the journal
transaction commits their reference. Unreferenced bytes do not constitute an
acknowledged session event.

Producer material requires its dedicated authority checks. The generic append
route rejects owner-only policy receipts, produced-run captures, runtime
configuration, and producer-owned material references.

## Append and replay

`PostgresSessionsJournal.AppendCoreAsync` performs the durable append:

1. Validate the event version, payload, identity, and references.
2. Open the service-owned database transaction and confirm the session exists.
3. Admit the scoped event ID through the consumer inbox.
4. For an exact duplicate, compare canonical content and return the existing
   event after required producer checks.
5. Persist producer bytes when the specialized writer supplies them.
6. Advance the run position and insert the event, references, and outbox record.
7. Revalidate specialized owner authority and commit.

Only committed rows appear in replay. Pages contain at most 200 events. Cursors
encode the stream scope and position; a session cursor cannot stand in for a
run-wide cursor or another session's cursor.

The native subscription implementation polls journal pages. Subscriptions have
event and duration bounds: at most 10,000 events and 300 seconds. Reconnect with
the returned opaque cursor to continue.

Gateway's public SSE path is separate. It reads committed run pages, projects
their contents, and rechecks Projects read authority before emitting each event.
Neither path depends on a process-local event channel for durable replay.

## Addressed-message lifecycle

Messages have a sender, recipient, thread, delivery mode, execution binding, and
expiry. Their delivery records are separate from workflow decisions.

1. **Admit.** Events checks the Orchestrator-owned route and outbox proof.
   Admission stores the message, journal evidence, and outbox entry together.
2. **Claim.** At a presenting turn boundary, Events reads the recipient's current
   owner binding and leases an eligible message.
3. **Present.** A current claim marks the message delivered to its addressed
   session.
4. **Acknowledge.** The recipient acknowledges the delivered message with the
   claim owner and fence.

Claims use increasing claim fences. Expired leases permit another claim without
granting the old claimant continued access. `Immediate` messages take priority
over queued messages, while the per-thread predicate preserves ordering.

Acknowledgment is receipt-only. The Orchestrator's separate gate operation decides
approval. Message acceptance also does not prove that AgentHost executed a turn.

## Session material and forks

The typed material interface accepts execution-bound content, not a caller-chosen
Blob key or URL. Writes verify current Projects authority and Orchestrator runtime
registration. Stored metadata binds content length, digest, SDK/runtime versions,
and model selection.

Reads start from a committed journal event and verify the recorded bytes.
Historical turn reads use current run-read authority and session membership.
SDK-cache reads retain the stricter run-selection boundary. Cache reuse also
requires compatible SDK and model bindings.

Private runtime configuration and reviewed remote-tool result material do not
appear in ordinary event responses. `PublicSessionEventProjection` removes the
private references and normalizes private tool events. Private remote-tool result
routes use their configured mutual-TLS owner boundary, not the generic material
route.

An explicit-event fork names a committed source cursor. Events verifies the
Orchestrator fork admission, preserves source history, and creates target session
lineage under the saved provider binding. A cursor is a history boundary, not
permission to repeat the source session's effects.

## Usage and project facts

Usage writes consume immutable Orchestrator source receipts by reference. The
consumer checks the source, pins Cost binding and rate-card identity, deduplicates
receipts, and commits accounting with its acknowledgment. A client-supplied token
count is not an authenticated SDK source receipt.

The usage ledger is separate from telemetry and journal replay. Missing pricing
stays explicit; it does not become a zero charge. Run admission and reconciliation
read priced totals through this boundary.

Project accepted-effect facts also have their own stream. A project-level
Knowledge change does not need a fabricated run or session identity.

## Failure behavior

- Invalid versions, malformed cursors, or a changed immutable event are rejected.
- Missing or changed pinned providers block access instead of selecting a
  replacement.
- Lost message claims and changed execution fences reject presentation or
  acknowledgment.
- Failed producer authority or integrity checks prevent material acknowledgment
  or disclosure.
- The host verifies the expected schema at startup. Migration uses the separate
  `--migrate` path; serving traffic does not silently initialize the schema.

## Continue reading

- [Orchestration](./orchestration.md) explains session and turn ownership.
- [Agent runtime](./agent-runtime.md) explains material and SDK receipt producers.
- [Persistence](./persistence.md) explains outbox and Object Store boundaries.
- [Events & Sessions architecture](../architecture/events-sessions.md) and
  [contracts and endpoints](../reference/contracts.md) record detailed contracts.
