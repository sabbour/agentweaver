# Events & Sessions journal

The v1 source contains an unpublished `Agentweaver.EventsAndSessions` service
candidate. It implements the initial native Sessions journal slice; it is not a
deployed platform service.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'" alt="Events & Sessions pins the native PostgreSQL provider per run, serves the ordered journal, and stores addressed messages admitted from the Orchestrator owner outbox for fenced presentation at turn boundaries. Generic callers cannot write PolicyEvaluation events without trusted Core provenance." />
  </a>
  <figcaption>The source connects the Orchestrator owner to the Events & Sessions journal and addressed-message store, with current authority resolved through Projects & Config. PostgreSQL is the journal authority; generic run-scoped append does not establish Orchestrator Core writer provenance, so PolicyEvaluation writes are rejected. The figure describes source behavior, not deployment topology.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.drawio'">Open editable draw.io source</a></p>

## Ownership and scope

`Agentweaver.EventsAndSessions` owns the event journal, session records, immutable
Sessions-provider bindings, object-reference retention metadata, and addressed-message
records in its PostgreSQL schema. The Orchestrator owns run/session relationships,
execution fences and turn state, coordination requests, parent notifications, and its
durable message outbox. The shared `Agentweaver.Persistence.Postgres` library supplies
the transactional outbox and inbox; it does not own these domain tables. The service
uses the existing provider catalog/resolver and telemetry helper. The provider-neutral
`ISessionsJournal` interface and `SessionSubscriptionRequest` contract live in
`Agentweaver.Abstractions`; this service owns their PostgreSQL implementation.

This source slice connects owner-validated addressed-message delivery to session
turn-boundary requests. It does not implement the rest of the proposed platform:
automatic AgentHost scheduling, a background delivery relay, gate approval decisions,
usage accounting, consistency manifests, MAF checkpoints, or Gateway/UI/MCP routes.
The broader Sessions and coordination design remains Proposed for those workflows.

## Addressed-message owner integration

The Orchestrator exposes protected root/child registration and session-scoped send,
turn-boundary, turn-completion, notification, and acknowledgment routes. Before each
operation it requires the current Projects & Config authorization context and accepted
run selection; it does not trust token claims alone for current permission. It writes
outbound messages to its durable owner outbox before synchronously admitting them to
Events & Sessions. Events asks the Orchestrator to verify that the complete message
matches that outbox record, that both sessions belong to the active run, and that
sender/recipient writer and execution fences are current. It persists the journal
reference and Events-side outbox/inbox transactionally.

At an explicit owner turn-boundary request, the Orchestrator asks Events to claim and
present the next addressed message. Events re-reads the current owner session binding
for every operation and permits claim or presentation only while the owner reports
`presenting`; acknowledgment remains available after that boundary under the current
execution and claim fences. The owner records delivery and exposes parent notifications.
The completed boundary result is stored with its request state version, so retrying the
same request returns the same result without claiming another message. A blocked session
can resume when a pending wake exists, and a wake received during presentation is not
lost. `progress` messages remain in sender history but are not claimable as recipient
input; idempotency is scoped to the sender run and session.
When a valid correlated reply is admitted, the owner moves the matching pending request
to `input_available`. Later acknowledgment records receipt only: it does not approve a
gate or complete child work.
Needs-input and error messages can set a pending wake. The owner drives these calls
synchronously; no background relay or automatic AgentHost scheduler is included.

The Broker-backed HTTP integration test exercises this path with a real Broker-issued
run token, disposable PostgreSQL, accepted Projects selection, root/child registration,
owner-outbox validation, delivery and journal mapping, turn-boundary presentation,
fenced acknowledgment, parent notification, and permission revocation. It is an
integration test of the source hosts, not evidence of a deployed service.

## Provider pin and session creation

The protected `POST /internal/sessions/{sessionId}` route validates a caller token
issued for the configured HTTPS identity issuer and audience. The authenticated
principal must have a GUID `sub` and exactly one `project_id`/`run_id` pair; the
Identity Broker adds that owner pair only for an active core grant. The host does not
require tenant or platform-role claims. The session ID is checked as a bounded
identity. The host resolves the exclusive Sessions provider using the catalog and
permitted project override, negotiates the live PostgreSQL database and migrated
schema, and pins only after successful negotiation.

The first session for a project/run persists the provider ID, adapter version, options
schema version and revision, resource ID and generation, and negotiated capabilities.
The binding is immutable across that run. Creating another session with a different
effective binding returns a conflict rather than replacing the stored choice.

Every append, replay, and live subscription reads the persisted binding and verifies
that the exact provider, version, options, resource, generation, and capabilities are
still available. A missing or changed binding fails explicitly; it never selects a
replacement default. Pin telemetry records bounded provider and capability data and a
hashed resource ID, not option values, credentials, payloads, or raw resource IDs.

## Journal durability and replay

Event contracts are versioned independently by schema and event version. Event
version 1 supports typed turn, tool-call, accepted-decision, accepted-effect,
artifact-reference, and cache-reference payloads. Event version 2 adds the
purpose-built `PolicyEvaluation` payload; existing version-1 payloads remain
appendable and replayable. The generic run-scoped append path rejects every
`PolicyEvaluation` event: matching the authenticated actor does not prove that
Orchestrator Core wrote the decision. The Orchestrator now owns current grants and
redacted receipt records, but the reserved receipt-backed Events consumer remains
separate #1846 work; generic append is still not a trusted writer path. The
event envelope binds project/run/session; its payload stores only bounded actor,
tenant/step, grant reference/revision, purpose/action/fence, typed
outcome/reason, and provider/options identity metadata. It has no free-form error
field or space for rule text, tool arguments, prompts, or credentials. Large
content in other event kinds is represented by opaque `ObjectKey` references with a
purpose, optional byte length, and retention metadata; the service does not store
referenced bytes or credentials.

Append serializes access to the project/run stream position in PostgreSQL, admits a
run-scoped stable event identity through the inbox, and writes the event,
object-reference metadata, position, and outbox record in the same transaction. Events
from different sessions in one run share a single contiguous sequence while retaining
their originating session identity. It commits before returning the event. A retry
with identical canonical event content returns the original event; reusing its
identity with different content in the same run is a conflict. PostgreSQL, not an
in-process counter or channel, assigns the authoritative ordered position.

When the reserved receipt-backed consumer is implemented, the action guard must
await a successful durable append before performing a protected effect. An append
failure is an error, never an allow; the event is evidence only and cannot grant
authority by itself. Until #1846 wires that consumer, guarded effects requiring
PolicyEvaluation journal evidence remain fail closed.

Run replay returns a bounded, run-ordered page spanning all sessions; session replay
returns only that session's events at their run positions. Run cursors are bound to a
project/run, while session cursors are additionally bound to a session. Live
subscriptions poll durable journal state across service instances and emit NDJSON
`SessionEventDelivery` records containing the event and its `nextCursor`. A client
reconnects by passing that cursor unchanged; the numeric position alone is not the
cursor contract.

## Schema and validation boundary

The host defaults to the service-owned `events_sessions` schema. Run the executable
with only `--migrate` to apply its embedded migration. That command requires a
separate privileged PostgreSQL connection and workload identity. Ordinary startup
uses the runtime Entra role and only verifies that the Events & Sessions and outbox
schemas are current; it fails when migration is needed and never creates or alters
database objects. Readiness repeats the verification.

Both connections require an explicit `WorkloadIdentityCredential` configuration.
Npgsql obtains a PostgreSQL-scoped token through its async password callback for each
new physical connection; Azure Identity manages token caching and refresh. The
connection strings must include their Entra PostgreSQL role and must not contain a
password. TLS uses `VerifyFull`. There is no developer-credential or password
fallback. The service does not create Azure resources, run a background relay, or
publish an image.

Use these separate settings:

| Configuration | Purpose |
| --- | --- |
| `ConnectionStrings:EventsAndSessions` | Runtime PostgreSQL endpoint, database, and least-privilege Entra role. |
| `EventsAndSessions:Database:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Explicit workload identity for runtime database access. |
| `ConnectionStrings:EventsAndSessionsMigration` | Migration PostgreSQL endpoint, database, and separately granted schema-owner role. |
| `EventsAndSessions:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Explicit workload identity for the migration command only. |

The migration job and its database grants are not provisioned by this source candidate.

The [contract reference](../reference/contracts.md) lists routes and configuration.
The [testing guide](../guide/testing.md) describes the disposable-PostgreSQL tests and
their limits. There was no earlier v1 Sessions event envelope, so no N-1 event-schema
compatibility claim applies to this initial version-1 contract.
