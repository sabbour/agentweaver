# Events & Sessions journal

The v1 source contains an unpublished `Agentweaver.EventsAndSessions` service
candidate. It implements the native Sessions journal and a separate project-scoped
accepted-effect fact stream; it is not a deployed platform service.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'" alt="Events & Sessions pins the native PostgreSQL journal, serves ordered run-event replay, appends Knowledge-owned accepted-effect receipts as project facts, and presents Orchestrator owner-outbox messages at fenced turn boundaries. The Orchestrator validates mapped child context against current decisions and authority. Events admits PolicyEvaluation only from immutable Orchestrator receipts after current authority and owner-session checks." />
  </a>
  <figcaption>Run-bound session events, project-scoped facts, and addressed messages have separate addresses and storage. Generic PolicyEvaluation appends are rejected; the receipt-reference route fetches immutable Orchestrator evidence and revalidates admission before commit. The Orchestrator validates mapped context against current owner state and authority. The figure describes source behavior, not a deployment topology.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.drawio'">Open editable draw.io source</a></p>

## Ownership and scope

`Agentweaver.EventsAndSessions` owns the run event journal, separate project-scoped
accepted-effect facts, session records, immutable Sessions-provider bindings,
object-reference retention metadata, and addressed-message records in its PostgreSQL
schema. The Orchestrator owns run/session relationships,
execution fences and turn state, coordination requests, parent notifications, and its
durable message outbox. The shared `Agentweaver.Persistence.Postgres` library supplies
the transactional outbox and inbox; it does not own these domain tables. The service
uses the existing provider catalog/resolver and telemetry helper. The provider-neutral
`ISessionsJournal` interface and `SessionSubscriptionRequest` contract live in
`Agentweaver.Abstractions`; this service owns their PostgreSQL implementation.

This source slice connects owner-validated addressed-message delivery to session
turn-boundary requests. It does not implement the rest of the proposed platform:
automatic AgentHost scheduling, a background delivery relay, gate approval decisions,
consistency manifests, MAF checkpoints, or Gateway/UI/MCP routes.
The broader Sessions and coordination design remains Proposed for those workflows.

## Typed session material

Events owns bounded `TurnContent` and `SdkCache` material through the existing
`IObjectStore` provider. This interface is not a general Blob API.
The caller supplies actual bytes and an existing execution identity, not an
object key, storage URL, container, or credential.

`POST /internal/sessions/{sessionId}/material` requires the original authenticated
bearer, current Projects authority, and the current Orchestrator runtime registration.
Events also requires the recorded SDK source and exact actor, tenant, project,
run, session, accepted selection, registration revision, and execution fence.
An observe credential alone does not authorize a material write.

Events generates the scoped object key and records the kind, SHA-256 digest,
byte length, SDK version, runtime version, and model binding.
Each object contains at most 1 MiB.
Events stores the bytes before it commits the journal event and reference.
It rechecks current authority after storage and database waits, including exact retries.
The same event and material return the original acknowledgment.
Changed material for the same event conflicts.
An object without a committed journal reference is not acknowledged material.

`GET /internal/sessions/{sessionId}/material/{eventId}/{kind}` reads only material
from a committed event in the authorized session.
Historical `TurnContent` requires current `ReadRunSelection` or actually supplied `ReadProjects` entitlement, exact signed project/run binding, and recorded session membership.
Project-summary access alone does not authorize material.
SDK-cache reads retain the stricter Core `ReadRunSelection` boundary.
These reads require the immutable Sessions provider binding, not a still-active execution registration.
Events checks the recorded digest and length, then rechecks authority before it returns bytes.
SDK cache restoration additionally requires compatible SDK and model bindings.
Material responses use `Cache-Control: no-store`.
The route accepts `turnContent` and `sdkCache`, plus their original CLR names.
Numeric or unknown kinds are denied.

`GET /internal/projects/{projectId}/runs/{runId}/sessions-provider-binding` returns the actual immutable Sessions consumer pin.
It requires existing current `ReadRunSelection` and exact signed project/run binding before and after retrieval.
The owner verifies its stored provider, options revision, resource generation, and negotiated capabilities.
The no-store response contains no option values or credentials.

## Usage storage and Copilot pricing

The service owns separate append-only `usage_ledger` and immutable
`usage_rate_cards` tables. Migration `005_copilot_usage.sql` adds these tables after
the session journal, addressed messages, project facts, and explicit session forks.
The migration preserves admitted version-4 schemas and the earlier version-2 project-fact layout.
Migration `006_native_sdk_usage.sql` adds cache-write values and permits unknown
request counts. Migration `007_native_usage_receipts.sql` adds immutable source
receipts and run-scoped Cost bindings.
Migration `008_dispatch_accounting_witness.sql` adds nullable dispatch and
accounting-revision fields for legacy-compatible entries, plus an immutable
source-completion record. Ordinary startup requires version 8.

`PostgresUsageLedger` implements the low-level `IUsageLedger` storage contract.
An entry records tenant, project, run, session, agent, model metadata, measurements,
the Cost binding, and the price.
Native submissions also retain the turn, SDK event ID, and complete SDK source snapshot.
Cache-read and cache-write measurements remain separate. Native request counts stay
null because the SDK callback does not report them. Nullable measurements remain
unknown rather than zero.

The runtime source mode comes from the immutable platform or project selection.
Hosted Copilot retains weighted nano-AIU under `copilot.nano_aiu`.
BYOK retains native token measurements under `byok.tokens`, without Copilot units or a hosted multiplier.
Without an admitted Cost provider, BYOK accounting remains `Unpriced`.
Unknown cost cannot satisfy a hard cost bound.

A transaction commits the rate card and usage entry before returning.
The accounting receipt binds the canonical SHA-256 hash, attribution, immutable
price, rate-card version, and commit timestamp. Identical retries return the original
receipt. Changed content for the same event ID conflicts.
Database triggers reject changes and truncation of history.
Statement-level guards also reject `TRUNCATE`, including dependent and multi-table
operations, on native source records, accounting receipts, and run-scoped Cost bindings.

The run-wide accounting cursor is allocated under the shared transaction lock from
the maximum committed revision across usage entries and source-completion records.
It follows commit order, not provider occurrence time. Exact retries retain their
original cursor, and rolled-back writes do not advance it. Legacy rows keep a null
cursor; the migration does not invent historical order. A new usage entry for a
dispatch with a stored source-completion record is rejected, while an exact event
replay still returns its original receipt.
`UsageRunTotals.Events` remains an event count, not an accounting cursor. A
dispatch witness may omit its `RunTotals` only for legacy payload compatibility;
every new positive witness must include the exact authorized, same-snapshot
Copilot/AIC ROOT RUN totals, not a dispatch-only subtotal. This service does not
yet expose a witness route.

Source completeness describes whether the authenticated producer's exact receipt
set is complete; it does not describe whether those receipts have a price. An
honest `Unpriced` accounting acknowledgment is not a zero-cost result and can
coexist with a complete source receipt set. Missing receipt or host-terminal/drain
proof remains `Unknown` or `Partial`. Events source completeness is not Core
retirement authority: Core must independently join the same dispatch's verified
source completion with its current host-terminal/drain proof, exact meter/unit and
rate coverage, represented charges, and a fresh compare-and-set before retiring
priced hard-credit exposure. This checkout persists the completion record but
does not expose a host-drain assertion or a source-completion ingestion route.

Totals retain separate meter-source and unit groups. A missing measurement makes
that measurement total unknown. An unpriced entry makes the run or agent pricing
incomplete. A valid hosted Copilot nano-AIU price counts as priced without optional
SDK identity or status data. The ledger retains supplied identity and status as
metadata. BYOK accounting still requires the `byok.tokens` meter, token units,
and input/output token counts. Rate changes never reprice earlier entries. Exact
totals that exceed the numeric range fail rather than round or wrap.

`CopilotCostProvider` prices SDK-reported `nano_aiu` values in AI credits (`AIC`).
One AIC contains `1_000_000_000` nano-AIU. Those reported units already include
model weighting. Only quotes with an explicit unweighted basis apply a model
multiplier. The adapter requires an immutable rate card and exact provider,
configuration, resource, and capability bindings.

The optional `AzureCostProvider` uses an explicitly configured, versioned
Azure Retail Prices rate card for model-scoped standard input/output token rates
in the declared currency. It performs exact decimal arithmetic without currency
rounding and does not fetch prices. Missing token/cache measurements, nonzero
cache categories, unknown models, and changed bindings remain `Unpriced`.
BYOK quotes are unsupported. Provisioned-throughput mode also remains `Unpriced`
until trusted usage-share evidence supplies the resource and time-window denominator.
The provider composition does not establish the positive BYOK receipt path; that
requires genuine producer admission in [#1921](https://github.com/sabbour/agentweaver/issues/1921).

`IUsageLedger.AppendAsync` remains a low-level storage contract without caller authorization.
The optional HTTP consumer has separate append, source-preflight, and reconciliation paths.
`POST /internal/sessions/{sessionId}/usage-receipts` accepts only `receiptId`;
callers cannot supply a source URL, SDK measurements, price, rate card, or accepted marker.
Events fetches the immutable receipt from its fixed HTTPS Orchestrator owner.
The HTTP client rejects redirects and preserves the original validated bearer.
`POST /internal/runtime/sources/{runtimeInstanceId}/cost-preflight` reads the
current owner registration and SDK source receipt, then pins/verifies the run's
cost binding and returns a quote result. This is not a dispatch source report.
`POST /internal/projects/{projectId}/runs/{runId}/usage-cost-reconciliation`
accepts exact Copilot receipt references and returns same-snapshot run totals.
Its `AccountingRevision` remains the legacy event count; this advisory API is
not the committed cursor or proof for Core budget retirement.

The Orchestrator source writer requires that bearer and an independent observe credential.
It checks the current registration, Core permission, accepted selection, active
session and turn, Environment lease, and Identity grant after database waits.
The actual SDK session supplies the effective model, catalog hash, SDK versions,
event identity, and nullable measurements.
The owner commits these values before it returns `RuntimeUsageSourceReceipt`.
The receipt validator rejects changed owner, SDK, model, catalog, event, and selection pins.

Events requires current `ReadRunSelection` for the exact signed project/run.
It resolves the meter source through the existing Cost catalog/resolver and pins
the run's first Cost binding or explicit unavailable reason.
The transaction commits the price, rate card, ledger, source hash, receipt, and
consumer inbox before it returns a no-store accounting acknowledgment.
It repeats current Core authority after transaction waits, including duplicate requests.
Failed revalidation rolls back the transaction.

The accounting acknowledgment proves a committed price, not runtime credential authority.
It remains separate from the source receipt.
Committed source receipts support explicit retries after restart.
Unavailable providers and missing measurements produce `Unpriced`, not zero cost.
`GET /internal/projects/{projectId}/runs/{runId}/usage` returns exact run and agent totals.
These routes are enabled only with `EventsAndSessions:RuntimeUsage:Enabled`.
The [AgentHost candidate](agenthost.md) waits for this committed accounting acknowledgment before turn completion.
This source has no background usage relay or cloud acceptance.

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

For mapped child work, the Orchestrator checks the latest root decision envelope under
the root-session lock before it creates the owner child and spawn outbox records. The
envelope must still authorize dispatch of the exact confirmed WorkPlan item, with the
current actor, accepted-selection hash, and execution fence and no pending gate. Child
registration and spawn recheck live Projects authority before transaction commit. The
read-only runtime-owner-context endpoint re-reads the child owner row and latest root
decision, then rechecks Projects authority and accepted selection before returning
agent/model/turn metadata. A changed owner row, decision version, mapping, fence, or
pending gate returns a conflict rather than stale context; it does not cancel or schedule
runtime work.

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
appendable and replayable. `PolicyEvaluation` events use a dedicated
receipt-reference route; the generic run-scoped append path still rejects every
such event because actor equality does not prove Orchestrator Core provenance.
`POST /internal/sessions/{sessionId}/policy-evaluations` accepts only an immutable
receipt ID. Events verifies the pinned provider capability, fetches the committed
receipt from the fixed Orchestrator owner, and asks that owner to validate the
receipt against current admission authority. Every outcome requires current Core
write authority and accepted selection, plus a matching actor/tenant and active owner
session/run with the same writer and execution fence. After the owner-row check can wait
on PostgreSQL locks, the owner reads current Core authority and selection again before
admission succeeds. For Allow, it also rechecks grant scope/revision, expiry, and fence.
Deny and Error receipts may reference an issued inactive grant, but remain immutable
owner facts and cannot authorize an effect.

Events repeats the owner admission check from inside the PostgreSQL transaction
before commit, including for an identical retry. The owner repeats current Core
authority and accepted-selection checks after owner-row lock waits. The transaction commits the
PolicyEvaluation event, inbox identity, run position, object references, and outbox
records together. If the recheck fails, those writes roll back. After commit, Events
returns a no-store acknowledgment containing the receipt identity and durable event
position; a new append returns `201`, an identical retry returns `200`, and
conflicting reuse fails. The event envelope binds project/run/session; its payload
stores only bounded actor, tenant/step, grant reference/revision, purpose/action/fence,
typed outcome/reason, and provider/options identity metadata. It has no free-form
error field or space for rule text, tool arguments, prompts, or credentials. Large
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

The Orchestrator receipt writer rechecks the actual request actor, current Projects
`acceptRunSelection` authority, and accepted selection after owner-row lock waits and
immediately before committing the owner receipt, after its grant-row lock and insert.
This also applies when a receipt ID already exists. An Allow also requires the exact
current grant, expiry, fence, and selection; Deny/Error may refer to an issued inactive
grant but still require current Core write authority and active owner session/run
authority. The Core action guard
then awaits the durable Events acknowledgment and rechecks current authority, grant
state, and fence after that await. Any failed owner check, journal append, or
post-ack recheck prevents the protected effect; evidence alone cannot grant
authority. Downstream protected-effect call sites are not claimed to be wired.

Run replay returns a bounded, run-ordered page spanning all sessions; session replay
returns only that session's events at their run positions. Run cursors are bound to a
project/run, while session cursors are additionally bound to a session. Live
subscriptions poll durable journal state across service instances and emit NDJSON
`SessionEventDelivery` records containing the event and its `nextCursor`. A client
reconnects by passing that cursor unchanged; the numeric position alone is not the
cursor contract.

## Project accepted-effect facts

`POST /internal/project-facts/accepted-effects` is separate from all session and run
journal routes. Its request contains only the receipt ID, schema version, and event
version. Events fetches the immutable committed receipt from the configured fixed
HTTPS Knowledge address; callers cannot supply a source URL, payload, or accepted
marker. Redirects are disabled.

Events requires the caller's validated issuer and subject to match the original
receipt, checks project/run bounds and rejects purpose-bound tokens. It forwards the
caller bearer and optional tenant selector to Knowledge and Projects & Config, then
requires fresh effective project `WriteProjects` permission. If a caller uses
separate existing audience-correct tokens, the Knowledge token is forwarded only in
the protected request to the fixed Knowledge owner; neither token is stored or
logged.

After validation, Events constructs the accepted-effect fact itself. In one
PostgreSQL transaction it admits the receipt ID through the shared inbox, assigns a
project-scoped sequence, and persists the immutable fact and acknowledgment. An
identical retry returns the same acknowledgment; changed receipt content for the
same ID conflicts. The acknowledgment binds the receipt ID and versions to the
native fact ID, project, and sequence. No fact or inbox receipt is committed alone.

These facts do not enter `session_events`, require or modify a Sessions provider pin,
or claim a run/session transition. Knowledge uses a caller-driven relay and marks its
outbox event delivered only after validating this durable acknowledgment. Failures
leave delivery pending for a fresh authorized caller retry; there is no unattended
relay worker.

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
| `EventsAndSessions:Knowledge:BaseAddress` | Required fixed absolute HTTPS Knowledge owner URI used to fetch committed accepted-effect receipts; redirects are rejected. |
| `ProjectsConfig:BaseAddress` | Required fixed absolute HTTPS Projects & Config URI used to recheck current project authority; redirects are rejected. |
| `ConnectionStrings:EventsAndSessionsMigration` | Migration PostgreSQL endpoint, database, and separately granted schema-owner role. |
| `EventsAndSessions:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Explicit workload identity for the migration command only. |

The migration job and its database grants are not provisioned by this source candidate.

The [contract reference](../reference/contracts.md) lists routes and configuration.
The [testing guide](../guide/testing.md) describes the disposable-PostgreSQL tests and
their limits. There was no earlier v1 Sessions event envelope, so no N-1 event-schema
compatibility claim applies to this initial version-1 contract.
