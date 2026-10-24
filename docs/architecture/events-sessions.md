# Events & Sessions journal

Use this reference to understand the v1 Events & Sessions source candidate.
An event journal stores ordered records of run activity; a project fact is a
separate project-scoped record of an accepted effect. The unpublished
`Agentweaver.EventsAndSessions` service implements both source boundaries, but
it is not a deployed platform service.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'" alt="Events & Sessions pins the native PostgreSQL journal and serves ordered run-event replay. Project history reads require fresh Projects read authority and preserve exact run-bound narrowing. Configured private remote tool result routes require direct mutual TLS before reading bodies. Events checks current Orchestrator proof, inventories keys before writes, commits references with retention deadlines, and deletes expired bytes or aged orphans while preserving journal history. It verifies reads before returning bytes. Generic and public routes do not expose private results. Native remote-tool dispatch remains disabled." />
  </a>
  <figcaption>Run-bound events, project facts, and addressed messages use separate storage. Project history reads recheck current project access; run-bound callers stay within their run. Private result routes require configured mutual TLS and current owner proof. Inventory precedes byte writes; cleanup removes expired bytes and aged orphans without deleting journal history. The figure describes source behavior, not an approved deployment.</figcaption>
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

The host binds the native PostgreSQL journal as the default and requires the full
`SessionsCapabilities.All` set. The shared capability identifiers live in
`Agentweaver.Abstractions`; optional provider implementations may advertise fewer
capabilities, but the current host does not route runs to a non-native journal.

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
Before writing bytes, Events commits an inventory row for the generated key.
It stores the bytes before the journal transaction commits the event and
reference, and records the reference's retention deadline in that same
transaction. Every committed object reference extends the inventory deadline,
including references committed by ordinary journal appends. A journal failure
leaves the object unreferenced and its inventory row available for cleanup; it
does not acknowledge the material. An append cannot create a reference to an
object that cleanup has already deleted.
It rechecks current authority after storage and database waits, including exact retries.
The same event and material return the original acknowledgment.
Changed material for the same event conflicts.
An object without a committed journal reference is not acknowledged material.
The Events owner checks inventory hourly. It deletes unreferenced objects after
a 24-hour grace period and deletes referenced object bytes after their recorded
retention deadline. Deletion preserves journal history and reference metadata;
reads of expired or missing bytes fail instead of rebuilding them. Migration
010 backfills inventory from committed references, but earlier unreferenced
objects cannot be discovered because `IObjectStore` has no listing contract.

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

`RemoteToolResult` remains denied on these generic material routes. The dedicated
`POST /internal/sessions/{sessionId}/remote-tool-results` write route and
`POST /internal/sessions/{sessionId}/remote-tool-results/read` route require
the configured direct mutual-TLS listener before they read the request body.
HTTP requests receive `runtime_remote_tool_result_https_required`.
Without the TLS configuration, HTTPS requests receive `runtime_remote_tool_result_route_unavailable`.
An HTTPS flag, forwarded header, actor token, or project permission alone cannot pass this check.
All handled responses use `no-store`.

Events accepts only the configured Orchestrator peer certificate on this listener.
It forwards the original actor bearer to the fixed Orchestrator owner.
`POST /internal/runtime/remote-tool-results/verify` reconstructs the selected tool
call and verifies its current native action grant and reserved tool slot.
Events also checks current Projects authority, actor, tenant, session provider,
runtime registration, execution fence, and native SDK source.
It repeats these checks around storage and journal waits, including exact retries.
Events stores result bytes before committing their journal reference.
An acknowledgment binds the full call proof, runtime identity, source, length, and digest.

Private reads require the same current authority and committed journal reference.
Events verifies the recorded proof, source, byte length, and digest before returning bytes.
The Orchestrator checks for a saved result after native action admission and before external dispatch.
It accepts only the exact recorded call proof; only the previously unknown result content class can differ from the read template.
It rechecks current authority before returning saved or newly acknowledged bytes.
The ordinary AgentHost material client cannot use these private routes.
Public event pages and streams continue to omit private result references and bytes.

## Private result TLS configuration

Each service requires its own trusted configuration section:
`Orchestrator:RemoteToolResults:Tls` or `EventsAndSessions:RemoteToolResults:Tls`.
Both require `Kestrel:Endpoints:RemoteToolResults:Url` with HTTPS and the exact `ListenerPort`.
Core also requires the runtime registration and native SDK source owners.
Events requires the configured session material container.
Missing sections leave private routes disabled; invalid sections stop startup.

| Setting | Required value |
| --- | --- |
| `LocalServiceName`, `PeerServiceName` | `Orchestrator` and `EventsAndSessions`, in the local and peer roles. |
| `LocalServicePrincipalId`, `PeerServicePrincipalId` | Distinct, explicit, nonempty service principal GUIDs. They map the configured services to the pinned certificates; callers cannot supply this mapping. |
| `LocalCertificatePemPath`, `LocalPrivateKeyPemPath` | Distinct absolute server-side paths to the existing PEM certificate and private key. |
| `LocalCertificateSha256` | Exact SHA-256 digest of the local certificate bytes. |
| `PeerCertificateSha256Pins` | One to four distinct SHA-256 digests for the trusted peer certificates. |
| `ListenerPort` | The dedicated local HTTPS port. |
| `PeerOwnerAddress` | The fixed HTTPS root address of the other service's dedicated listener. |

Certificates must be current, non-CA certificates with client and server authentication purposes.
The native TLS stack checks the peer chain; outbound calls also require a valid server name.
TLS 1.2 or 1.3, revocation checks, certificate pins, and redirect rejection remain required.
The service keeps its certificate private; it does not give it to AgentHost or accept caller-selected owner addresses.
Bounded HTTP reads retain their deadline after response headers arrive.
A `409` owner response is an explicit changed-binding denial, not a successful retry.

This source adds no certificate issuance, service principal, trust store, deployment, or approved live TLS configuration.
Environment has a separate, optional current-call verifier profile:
`Orchestrator:RemoteMcpCallVerification:Tls`, with local service `Orchestrator`
and peer `Environment`. It requires the runtime registration owner and the
dedicated `Kestrel:Endpoints:RemoteMcpCallVerification:Url` port. It uses key
`remote-mcp-call-verification-environment`, not the Events profile.
The current-call route remains `/internal/runtime/remote-tool-results/verify`.
The actual server listener port selects the configured profile; request bodies
and headers cannot select a service role. Wrong transport, listener, or peer
denies the request before proof-body reads. If both profiles exist, they must
name the same Orchestrator principal and different peer principals and pins.
The existing Events listener, options, and peer gate are unchanged.

Environment's matching client uses `Environment:RemoteMcpCallVerification:Tls`,
key `remote-mcp-call-verification-orchestrator`, and client
`RemoteMcpCallVerificationOwner`. It pins the fixed owner address and has a
15-second timeout. The verifier still checks the original actor, current
registration, exact accepted call, current grant and policy, and existing
journal acknowledgment. Its receipt is not network evidence or a credential
grant.

Native remote-tool dispatch remains disabled.
Positive native TLS, complete producer recovery, and private-result acceptance remain open.

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
receipts and run-scoped Cost bindings. Migration `008_row_lock_record_updates.sql`
rejects every row update to `sessions`, `session_provider_bindings`, and
`session_fork_lineage`. It permits inserts and row locks without permitting data changes.
Ordinary startup requires version 8 and checks each enabled, unconditional update trigger.
It rejects missing, disabled, conditional, column-limited, or wrong-function triggers.
The new migration does not change deletion rules or rewrite earlier migration records.

`PostgresUsageLedger` implements the low-level `IUsageLedger` storage contract.
An entry records tenant, project, run, session, agent, model metadata, measurements,
the Cost binding, and the price.
Native submissions also retain the turn, SDK event ID, and complete SDK source snapshot.
Optional `A2AMessageId` and `SdkAccounting` metadata participate in the canonical hash.
Absent metadata preserves the historical canonical payload and hash.
Accounting metadata retains the native source session, positive sequence, usage ID,
reported credit status, and whether the SDK actually supplied that status.
Cache-read and cache-write measurements remain separate. Native request counts stay
null because the SDK callback does not report them. Nullable measurements remain
unknown rather than zero.

The runtime source mode comes from the immutable platform or project selection.
Hosted Copilot retains its `copilot.nano_aiu` source identity, while pricing uses
the SDK's reported input, cached-input, cache-write, and output token counts.
BYOK retains native token measurements under `byok.tokens`, without Copilot units
or a hosted multiplier.
Without an admitted Cost provider, BYOK accounting remains `Unpriced`.
Only priced usage counts toward Copilot credit limits. Unpriced usage remains
visible with its reason and event count; it is not treated as zero-priced usage.

The standalone `AzureCostProvider` accepts an explicitly configured, versioned
rate card with model-scoped standard input/output token rates and a declared
currency. It performs exact decimal arithmetic, does not fetch live prices, and
does not quote BYOK work. Missing token counts, unsupported cache categories,
unknown models, and changed bindings remain `Unpriced`. Events must also verify
trusted Azure provider and deployment facts against the accepted runtime model
pin before it can price a source receipt. `RequireTrustedAzureByok` performs that
check. The receipt consumer applies the same source checks, without the dispatch
message ID, to every receipt that claims Azure BYOK and rejects a failed claim, so
untrusted usage never enters the ledger. Native SDK usage without a dispatch
message ID is admitted as `Unpriced`. Events keeps these token totals but does not
assign a cost because it cannot attribute the usage to a dispatch.

Azure BYOK pricing is off by default. To turn it on, configure the
`EventsAndSessions:Cost:Azure` section with `AzureCostProviderOptions` fields and
an explicit `RateCard`. Events then registers the Azure Cost provider for the
`byok.tokens` meter source, pins the binding per run, and prices only Azure BYOK
receipts that pass `RequireTrustedAzureByok`. Uncorrelated native usage stays
`Unpriced`. Other BYOK providers stay `Unpriced` with `byok-provider-unpriced`.
A changed rate card never reprices earlier rows. New usage in a run that pinned an
earlier card stays `Unpriced` with `pinned-cost-binding-unavailable`.

Provisioned-throughput (PTU) usage stays `Unpriced` per event.
`AzureProvisionedThroughputAllocator` builds a report that splits one fixed PTU
period charge across ledger events by input plus output token share. It uses exact
integer minor units, assigns leftover units by largest remainder, breaks ties by
event ID, and records the rounding method. The shares always add up to the charge.
The report never changes ledger rows.

### Represented spend evidence

`POST /internal/projects/{projectId}/runs/{runId}/usage/spend-evidence` returns
`UsageSpendEvidence` for one (meter source, unit) bucket. The caller needs the same
read authority as the usage totals route. The request names the bucket, an
optional project period with an inclusive start and exclusive end, and at most 64
dispatch IDs. One repeatable-read snapshot supplies:

- Priced amount, priced event count, and unpriced event count for the run and the
  project period. Unpriced events without a unit count in every unit bucket of
  their meter source.
- An agent total for the run when the request names an `AgentId`.
- One `UsageDispatchAccountingWitness` per requested dispatch, with the source
  receipt identities, receipt digest, and known priced subtotal. A witness
  matches the dispatch in any run of the project, because a project budget holds
  exposure for dispatches from other runs.
- `AccountingCursor`, the number of ledger events for the project and meter
  source in that snapshot.

The ledger is append-only, so a later read has an equal or higher cursor and
includes every event an earlier read included. Events never reports
`SourceComplete`. A witness is `Partial` when it has receipts and `Unknown` when it
has none. Orchestrator compares the witness receipts with its own dispatch
completion manifest to decide whether every charge is represented. The route
reads the existing ledger and needs no new migration.

When `EventsAndSessions:RuntimeUsage:Enabled` is true, Events requires the same
server-owned `AgentHost:ModelBindings` map and `AgentHost:ModelBindingsRevision` as AgentHost.
It uses the SDK-independent shared resolver, not a separate Events model catalog.
The concrete model ID and canonical map identity can therefore match an accepted runtime pin.
This configuration does not by itself prove live model availability.
The optional `copilot-run-admission` route accepts only the accepted-selection hash.
Events reads the actual selection from its fixed Projects owner with the original bearer.
It requires current `ReadRunSelection` and `AcceptRunSelection` for the exact signed project/run.
The shared resolver supplies the concrete enabled hosted model; caller model IDs are not accepted.
The existing Cost lock and transaction bind the zero-work quote and Copilot-only totals.
Events rereads Projects selection and authority before committing that Cost pin.
An explicit `Unpriced` quote for a model without a published rate does not block run
admission or dispatch. It remains distinct from a zero-price estimate; malformed or
tampered pricing receipts still fail closed. No SDK session, usage event, source receipt,
or ledger row is invented during admission.
Orchestrator persists the typed admission receipt with root acceptance through its existing outbox.
Backlog claim uses the same acceptance transaction.
An identical accepted-root replay uses that immutable receipt without repricing earlier admission.
Configured Copilot credit limits cannot use BYOK token pricing or an unbound hosted connection.
The source paths do not prove live PostgreSQL, native availability, or deployment; each still requires separate acceptance.

A transaction commits the rate card and usage entry before returning.
The accounting receipt binds the canonical SHA-256 hash, attribution, immutable
price, rate-card version, and commit timestamp. Identical retries return the original
receipt. Changed content for the same event ID conflicts.
Database triggers reject changes and truncation of history.
Statement-level guards also reject `TRUNCATE`, including dependent and multi-table
operations, on native source records, accounting receipts, and run-scoped Cost bindings.

Totals retain separate meter-source and unit groups. A missing measurement makes
that measurement total unknown. An unpriced entry makes the run or agent pricing
incomplete. Rate changes never reprice earlier entries. Exact totals that exceed
the numeric range fail rather than round or wrap.

Hosted Copilot entries are priced from their complete token measurements against the
provider's pinned published rate card, not by applying a model multiplier to nano-AIU.
Complete token measurements remain sufficient when the SDK omits Nano AIU; optional
SDK accounting identity and status do not control pricing completeness.
Supplied identity text, source session, and positive sequence remain validated.
BYOK pricing uses the actual `tokens` measurement shape and the existing price disposition.
Hosted credit status does not determine BYOK token-pricing completeness.
Neither a fully priced subtotal nor a native usage watermark proves complete dispatch membership.

`CopilotCostProvider` prices reported input, cached-input, cache-write, and output
tokens directly in AI credits (`AIC`). Its immutable provider-supplied rate card
records per-million-token USD rates for default and long-context tiers, the
long-context threshold, `1 AIC = $0.01 USD`, and the source URL, snapshot version,
and retrieval date. The current snapshot is sourced from
[GitHub's official Copilot model-pricing documentation](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing),
retrieved 2026-10-17. The default snapshot contains the 33 models listed on that
page. The operator supplies its strict, versioned JSON through
`EventsAndSessions:Cost:Copilot:RateCard:Json` in runtime configuration. Startup
validates the schema and hashes the exact JSON text; each cost binding, snapshot,
and accounting receipt retains the rate-card version and content hash. Updating the
ConfigMap and rolling out the service loads new rates without a product rebuild.
Keep the adapter options revision aligned with the provider catalog; the rate-card
version changes independently when pricing data changes.
No runtime price fetch, scalar multiplier, or inferred price is used. Unknown
model rates, missing token categories, and unavailable context-tier rates remain
explicitly `Unpriced` with a reason; this does not block admission or dispatch.
Unpriced usage is recorded and visible but excluded from credit-limit totals.
Known priced usage continues to count. The adapter requires an immutable rate-card
snapshot and exact provider, configuration, resource, and capability bindings.

`IUsageLedger.AppendAsync` remains a low-level storage contract without caller authorization.
The optional HTTP consumer uses a different, reference-only admission path.
`POST /internal/sessions/{sessionId}/usage-receipts` accepts only `receiptId`.
Callers cannot supply a source URL, SDK measurements, price, rate card, or accepted marker.
Events fetches the immutable receipt from its fixed HTTPS Orchestrator owner.
The HTTP client rejects redirects and preserves the original validated bearer.

The Orchestrator source writer requires that bearer and an independent observe credential.
It checks the current registration, Core permission, accepted selection, active
session and turn, Environment lease, and Identity grant after database waits.
The actual SDK session supplies the effective model, catalog hash, SDK versions,
event identity, and nullable measurements. The pinned SDK does not provide a proven
binding from usage events to an A2A send. As a result, the runtime leaves
`A2AMessageId` null. It does not infer ownership from the active turn or event
timestamps. Events accepts cost-turn evidence only when a source receipt's A2A ID
exactly matches `CostTurnId`. A callback without that ID cannot release the cost
turn. A callback from an earlier send can arrive during a later
send. Null correlation is omitted from legacy
source payloads, preserving their canonical hashes. The receipt validator rejects
changed owner, SDK, model, catalog, event, and selection pins.

Events requires current `ReadRunSelection` for the exact signed project/run.
It resolves the meter source through the existing Cost catalog/resolver and pins
the run's first Cost binding or explicit unavailable reason.
Before runtime readiness, Events can preflight that immutable binding against the
effective model from the current Orchestrator-owned SDK source snapshot. It uses
the existing Cost provider quote path without recording usage or exposing the
quote amount. A valid explicit `Unpriced` result, including a model without a
published rate, remains visible but does not prevent readiness or dispatch.
The SDK source still does not prove per-turn usage-to-A2A affinity for arbitrary or
legacy callbacks. Workflow-bound positive-hard-capped Hosted Copilot activation uses
the prepared dispatch and begin path with current whole-run priced totals; it denies
the next dispatch when observed totals reach the hard limit. A whole-run total does not
identify one turn or establish accounting finality. An unbound positive-cap
registration still fails with `runtime_usage_affinity_unavailable` before readiness.
Any already-started turn with unknown ownership remains held; its native receipt is
retained and cannot release a cost turn without the exact message ID.
The transaction commits the price, rate card, ledger, source hash, receipt, and
consumer inbox before it returns a no-store accounting acknowledgment.
It repeats current Core authority after transaction waits, including duplicate requests.
Failed revalidation rolls back the transaction.

The accounting acknowledgment proves a committed price, not runtime credential authority.
It remains separate from the source receipt.
Committed source receipts support explicit retries after restart.
Unavailable providers and missing measurements produce `Unpriced`, not zero cost.
`GET /internal/projects/{projectId}/runs/{runId}/usage` returns exact run and agent totals.
`POST /internal/projects/{projectId}/runs/{runId}/usage/copilot-cost-snapshot` returns
the existing run-pinned binding, a zero-work quote for the actual SDK model, and Copilot-only totals.
It uses the same Cost lock and transaction as native receipt ingestion.
This quote does not create a usage event or accounting receipt.
An empty observed set has a zero priced subtotal without turning an unpriced quote
into a priced zero.
Unpriced Copilot entries remain incomplete and visible, but are excluded from the
priced subtotal used by Copilot credit limits. Other meters do not affect Copilot credit limits.
The snapshot endpoint rechecks current signed run authority before it commits.
An optional dispatch ID and at most 512 required references request an observed accounting join.
Events reads each immutable source receipt, accounting receipt, and ledger row in the same transaction.
Each registration, SDK source, platform message, event, accounting hash, and stored price must match.
An optional `NativeTurn` reference requires an authenticated read of Core's persisted native turn.
Events reads that proof before taking the Cost lock and again after releasing it.
The proof must retain the current registration, execution fence, checkpoint, report hash, revision, and exact event set.
Only this verified association permits a raw usage receipt without a platform message ID.
A wrong non-null message ID or an event after native completion fails.
Without native proof, the existing exact raw-message check remains unchanged.
The response returns only those verified references as `RepresentedReceipts`.
Missing rows reject the join; altered references cannot substitute another receipt.
For hosted-Copilot snapshots, run and per-agent totals must include the amount and
priced or unpriced event count for every represented receipt, and run totals must
match agent subtotals. Turn-cost reconciliation keeps a valid `Unpriced` receipt in
the represented set with its reason and event count; it does not block reconciliation
or become a zero-cost priced event. Missing rows, altered references, and totals that
omit represented amounts or event counts are rejected.
Admission and credit-limit validation accept well-formed `Unpriced` quote and usage
shapes, retain their reasons and event counts, and use only the known priced subtotal;
malformed or tampered receipts remain rejected.
This path neither fetches a Source GET manifest nor writes synthetic usage.
These routes are enabled only with `EventsAndSessions:RuntimeUsage:Enabled`.
The [AgentHost candidate](agenthost.md) waits for the observed join and durable owner completion before it returns workflow output.
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
runtime work. The runtime-execution-observation endpoint uses the same current-owner
checks to read only the child’s own checkpoint status and dispatch metadata. It hashes
completed result text and returns only the digest and UTF-8 length; it never returns the
result text itself. It can observe active, completed, or failed child sessions under the
current execution fence; terminal state does not bypass owner, decision, or selection
checks. Observation does not retry or schedule runtime work.

The version-2 runtime-execution-observation route keeps those checks and wraps the unchanged
version-1 observation with a `checkpointParent` object. Its required
`hasParentMetadata` flag and nullable `parentCheckpointId` come from the same selected latest
checkpoint row. True/null identifies a verified root; false/null means unknown legacy
metadata. Contradictory or invalid saved parent data returns a conflict. The version-1
route is unchanged.

At an explicit owner turn-boundary request, the Orchestrator asks Events to claim and
present the next addressed message. Events re-reads the current owner session binding
for every operation. It permits claim or presentation only while the owner reports
`presenting`. Acknowledgment remains available after that boundary under the current
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

The Broker-backed HTTP integration test uses a real Broker-issued run token,
disposable PostgreSQL, and an accepted Projects selection. It tests root/child
registration, owner-outbox validation, delivery and journal mapping,
turn-boundary presentation, fenced acknowledgment, parent notification, and
permission revocation. It is an
integration test of the source hosts, not evidence of a deployed service.

## Provider pin and session creation

The protected `POST /internal/sessions/{sessionId}` route validates a caller token
issued for the configured HTTPS identity issuer and audience. The authenticated
principal must have a GUID `sub` and exactly one `project_id`/`run_id` pair; the
Identity Broker adds that owner pair only for an active core grant. The host does not
require tenant or platform-role claims. The session ID is checked as a bounded
identity. The host resolves the exclusive Sessions provider through the catalog and
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
Deny and Error receipts can reference an issued inactive grant, but remain immutable
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

Append serializes access to the project/run stream position in PostgreSQL and admits a
run-scoped stable event identity through the inbox. It writes the event,
object-reference metadata, position, and outbox record in the same transaction. Events
from different sessions in one run share a single contiguous sequence while retaining
their originating session identity. It commits before returning the event. A retry
with identical canonical event content returns the original event; reusing its
identity with different content in the same run is a conflict. PostgreSQL, not an
in-process counter or channel, assigns the authoritative ordered position.

The Orchestrator receipt writer rechecks the actual request actor, current Projects
`acceptRunSelection` authority, and accepted selection after owner-row lock waits. It
rechecks them immediately before committing the owner receipt, after its grant-row
lock and insert. This also applies when a receipt ID already exists. An Allow requires
the exact current grant, expiry, fence, and selection. Deny/Error can refer to an
issued inactive grant, but still require current Core write authority and active owner
session/run authority. The Core action guard
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
leave delivery pending for a fresh authorized caller retry. The service has no unattended
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
password. TLS uses `VerifyFull`. The service has no developer-credential or password
fallback. It does not create Azure resources, run a background relay, or
publish an image.

Use these separate settings:

For provider selection and runtime setting guidance, see the planned
[provider configuration guide](../guide/configure-providers.md) and
[runtime configuration reference](../reference/runtime-configuration.md).

| Configuration | Purpose |
| --- | --- |
| `ConnectionStrings:EventsAndSessions` | Runtime PostgreSQL endpoint, database, and least-privilege Entra role. |
| `EventsAndSessions:Database:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Explicit workload identity for runtime database access. |
| `EventsAndSessions:Knowledge:BaseAddress` | Required fixed absolute HTTPS Knowledge owner URI used to fetch committed accepted-effect receipts; redirects are rejected. |
| `ProjectsConfig:BaseAddress` | Required fixed absolute HTTPS Projects & Config URI used to recheck current project authority; redirects are rejected. |
| `ConnectionStrings:EventsAndSessionsMigration` | Migration PostgreSQL endpoint, database, and separately granted schema-owner role. |
| `EventsAndSessions:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Explicit workload identity for the migration command only. |

The migration job and its database grants are not provisioned by this source candidate.

## Events runtime input validation

The complete v1 product installation, including the Events Deployment, Service,
ServiceAccount, runtime ConfigMap, and Cilium policy, is maintained in the root
Kustomization at `deploy/k8s`. The static workload pins its release image through
Kustomize; the install does not generate a Deployment or Service from a
JavaScript profile.

`deploy/k8s/profiles/events-and-sessions/render.mjs` validates operator-selected
runtime values before they are written to the checked-in
`events-and-sessions-runtime-config.json`. It requires the admitted P0 database
and workload identity, Sessions resource identity and generation, Broker
issuer/audiences, private Knowledge/Projects/Orchestrator owner addresses, a
Blob container in `agentweaverblob`, and valid model-binding and rate-card values.
Database access remains password-free with `VerifyFull`; credentials, SAS
tokens, extra Blob paths, and encoded paths are rejected. This validator emits
configuration data only. It does not produce a Kubernetes List, image,
Deployment, Service, policy, Secret, or certificate.

After reviewing a configuration update, render the complete bundle and apply
it through Kustomize:

```powershell
kubectl kustomize deploy\k8s
kubectl apply -k deploy\k8s
kubectl rollout restart deployment/events-and-sessions -n agentweaver
kubectl rollout status deployment/events-and-sessions -n agentweaver
```

Kubernetes does not restart a process when data in a referenced ConfigMap
changes, so restart the affected workload after changing environment-backed
configuration. The release helper validates receipt-pinned images and all
operator-owned prerequisites before applying the same bundle. It does not
create the operator-supplied TLS Secret, public CA data, database grants,
migrations, or Gateway placement. The private `RemoteToolResults` listener
remains disabled.

The [contract reference](../reference/contracts.md) lists routes and configuration.
The [testing guide](../guide/testing.md) describes the disposable-PostgreSQL tests and
their limits. No earlier v1 Sessions event envelope exists, so no N-1 event-schema
compatibility claim applies to this initial version-1 contract.
