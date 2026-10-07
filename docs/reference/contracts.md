# Contracts, endpoints, and configuration

## Provider contract

`ProviderDescriptor` contains a `ProviderSeam`, provider ID, adapter version, options-schema version, hosting pattern, and advertised capabilities.

`ProviderRegistration` adds the enabled state, options revision, and selected schema version. A `PinnedProviderBinding` records the run, resource generation, and negotiated capabilities.

These records contain no credentials or option values. See [providers and models](../architecture/providers-models) for resolver limits.

`ProviderMeterSourceSelection` maps an explicit source to one enabled Cost provider.
`CostProviderResolutionRequest` requires an exact adapter version, options schema
and capability set. `ResolveCost` returns a checked `CostProviderResolution`
candidate, not a live resource or pricing result.

Projects & Config loads optional `ProjectsConfig:ProviderCatalog:MeterSourceSelections`
entries containing `MeterSource` and `ProviderId`. Native
`PUT /api/projects/{projectId}/runs/{runId}/selection` accepts a Cost
`ProviderRequirement.MeterSource`; native selection GET returns that key in the
stored `EffectiveProviderSelection`. Both retain current owner authorization.
No new usage-writing endpoint, model-source authority, or pricing API is introduced.

## Cost and usage storage contracts

`CostBinding` records the meter source, provider identity, adapter version,
configuration revision, resource generation, negotiated capabilities, and immutable
`CostRateCard`. `PinCost` rejects changed candidate configuration. `VerifyCost`
rejects unavailable or changed provider identity. The adapter verifies the resource
generation and rate-card content.

`ICostProvider.Price` returns an amount, unit, rate card, and `CostDisposition`.
`Quote` requires an explicit weighted or unweighted basis. The Copilot adapter
converts reported nano-AIU to AIC without a second model multiplier. Missing
measurements or model rates return `Unpriced` with a reason and no amount.
These methods do not authorize a model session or caller.

`UsageSubmission` contains an event ID, occurrence time, attribution, model
metadata, and nullable measurements. `IUsageLedger.AppendAsync` validates and
commits the immutable entry and rate card before returning. It returns the original
entry for an identical retry and rejects changed event or rate-card content.
`UsageAccountingReceipt` binds the event ID, attribution, canonical SHA-256 hash,
price disposition, amount, unit, rate-card version, and commit timestamp.
The ledger commits before returning this receipt. A retry returns the original
receipt without repricing. This receipt does not authorize an SDK producer.
`GetRunTotalsAsync` returns exact agent totals and separate meter-source/unit
amounts. Unknown measurements stay null. Incomplete pricing remains explicit.
Native submissions retain `TurnId`, `SdkEventId`, and the complete `SdkSource`
snapshot. Cache-read and cache-write measurements remain separate. Native callbacks
do not supply a request count, so `RequestCount` stays null.

`RuntimeUsageSourceReceipt` contains the immutable runtime registration, native
usage submission, source hash, receipt ID, version, and recorded timestamp.
`RuntimeUsageSourceReceiptContract` validates exact owner, SDK, model, catalog,
event, turn, and accepted-selection pins. It rejects BYOK and changed hashes.
This receipt proves a source observation, not a price or accounting acknowledgment.
A stored source receipt remains readable after its original lease expires.
That historical read does not authorize another observation.

`PostgresUsageLedger.AppendWithinTransactionAsync` uses the caller's owned
PostgreSQL transaction without a separate commit. The trusted Events consumer
can commit its source inbox and accounting receipt with the usage entry and rate card.
The ordinary `AppendAsync` method retains its own transaction and commit.

These low-level contracts do not authenticate a remote writer.
Migration `006_native_sdk_usage.sql` extends the service schema to version 6.
It preserves existing history and adds cache-write values and nullable request counts.
Migration `007_native_usage_receipts.sql` adds the reference consumer's immutable
receipts and run Cost bindings in version 7.
The optional native HTTP routes separately authenticate the original bearer
and require current Core authority. Source writes also require Identity's observe credential.
Typed action grants and opaque model references cannot replace this producer boundary.

## Orchestrator AGT Policy provider

`AgtPolicyProviderOptions` supplies an opaque resource ID and generation, an options revision and schema
version, and one or more platform AGT YAML policy documents. Every platform document must parse, default
to deny, and use only `allow` or `deny` actions. The provider is enabled as the platform singleton
`agt.dotnet-yaml`; project provider overrides are rejected by the shared resolver.

`AgtPolicyEvaluationRequest` carries a bounded opaque actor ID and action ID, primitive policy context,
and optional project policy documents. Each configured document is evaluated independently; every
document in both policy sets must allow, so project policy can only narrow. The adapter returns typed allow, deny, or error
evidence plus provider/options metadata; it does not consume grants or authorize effects. The
`ExecutableActionGuard` uses the Orchestrator-owned current-grant lookup and redacted receipt writer. It
returns structured deny/error results for missing, stale, mismatched, expired, or unavailable grants and
rechecks current authority, grant state, and fence after awaited operations before invoking a protected
callback. The Orchestrator receipt writer rechecks the authenticated actor and current
`acceptRunSelection` authority/selection immediately before committing the owner receipt.
Allow receipts also require the exact current grant, expiry, and fence; Deny/Error need
only an issued grant and do not grant authority. For an Allow, the guard waits for the
durable Events & Sessions receipt acknowledgment before the protected callback; Events
accepts only the immutable receipt reference, revalidates current owner admission inside
the journal transaction, and returns a no-store acknowledgment. Downstream
protected-effect call sites are not claimed.
Caller bindings require one authenticated identity whose `sub`, `project_id`, and `run_id` claims share one
validated HTTPS issuer. The current grant descriptor must match that issuer and subject; a missing, duplicate,
or cross-issuer binding is denied before protected effects. The owner resolves fresh tenant membership and
current grant authority; a tenant claim is not required.
## Orchestrator coordination owner

The `Agentweaver.Orchestrator` is an unpublished .NET 10 host candidate. Protected
coordination routes require an OpenIddict-validated bearer with one authenticated GUID
`sub`, one project/run binding, `api.read` and `projects.orchestrator` scopes, and the
route's configured audience. Each operation also revalidates current Projects & Config
authority and the accepted run selection; the caller's claims alone do not establish
current permission. Internal Events calls use the configured Events audience and the
same caller bearer.

Typed action mutations use strict request contracts, expected state versions, and
idempotency keys. Proposing or revising non-empty or fixed work requires the server's
accepted Sandbox binding; a missing registered adapter or negotiation returns `503`.
The registered adapter resolves, but does not provision or release, an existing
resource. Projects authority is refreshed after resolution, then the accepted
selection and execution fence are rechecked by the owner CAS. The immutable binding
is committed with the winning decision, gate, grant, and outbox transaction. The
caller cannot submit a resource pin or override the durable binding.

| Method and path | Contract |
| --- | --- |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/root` | Accept the root session for the current accepted run selection and register it with Events & Sessions. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions` | Read the current typed decision state and pending gate without exposing a transferable authorization or provider pin. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/propose_outcome_spec` | Propose a schema-validated outcome specification. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/select_workflow` | Select a workflow from the authorized catalog or submit a generated definition for confirmation. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/propose_work_plan` | Validate a plan against the selected workflow, current role/model eligibility, and the accepted Sandbox binding before opening its confirmation gate. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/revise_work_plan` | Validate a bounded revision against the same immutable run context; scope changes require a gate. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/request_assembly` | Record a typed assembly request against an accepted workflow, plan, and platform step. It does not execute assembly. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions/gates/{requestId}/answer` | Answer an exact pending gate with one of its allowed choices. Message receipt is not an answer. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{parentSessionId}/children` | Register a child under the active parent and register the child session with Events & Sessions. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{parentSessionId}/spawn` | Spawn and register a child. When mapped to a WorkPlan item, the Orchestrator checks the latest confirmed root decision under the owner lock and rechecks current Projects authority before committing the child and spawn outbox. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/tree` | Read the current owner session-tree snapshot after checking Projects authority and accepted selection; returns `Cache-Control: no-store`. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/status` | Read the owner's session status, blockers, lifecycle, and runtime-effect availability; unavailable activity/effects are reported explicitly. The accepted selection is rechecked before response. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/fork` | Accept a typed request containing `executionFence`, `idempotencyKey`, `targetSessionId`, `sourceEventId`, `sourceCursor`, and non-coordinator `kind`. The Orchestrator durably reserves the exact request and accepted-selection hash, then rechecks current Projects authority after its owner SQL waits before committing that reservation. Events must obtain an owner admission receipt for the current actor, selection, and fence before journal work and immediately before commit. The Orchestrator registers returned lineage only after its final owner recheck after child, request, command, and outbox writes; if authority is revoked after Events commits, owner registration writes roll back and the result is explicitly unregistered. Returns `201` for a new registration, `200` only for an authorized exact registered replay, `403` for current permission revocation, or `409` for stale/conflicting admission or owner state. An unregistered target is not a usable child. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/messages` | Persist a fenced owner message and synchronously admit it to Events against the exact persisted outbox record. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/turn-boundary` | Advance the logical turn, claim and present the next eligible addressed message, and return pending parent notifications. A retry with the original state version returns the completed boundary result. A blocked session can resume only when a wake is pending. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/turn-completion` | Record an explicit idle, blocked, or completed turn state under the current fence and state version. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/status` | Read the accepted run's owner state, current execution fence and state version, and any active failure cause/reference. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/turn-failure` | Report a logical `failed` or `indeterminate` turn with idempotency key, cause/reference, current run version, session version, and fence. The owner advances the run fence, marks other active turns indeterminate, persists the operation and outbox event atomically, and does not claim physical effects. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/recovery` | Recover a failed/indeterminate run using its current fence/state version plus an idempotency key and recovery cause/reference. The owner advances the fence again, resets only failed/indeterminate logical turns to idle, and commits a recovery outbox event; it does not replay or compensate physical effects. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/notifications?limit={limit}` | Read unacknowledged parent notifications; the limit defaults to 50 and is bounded to 100. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/notifications/{notificationId}/acknowledge` | Acknowledge a notification belonging to this parent session. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/messages/{messageId}/acknowledge` | Acknowledge a delivered message using its claim fence; receipt is not gate approval or work completion. |
| `POST /internal/projects/{projectId}/runs/{runId}/coordination/message-route` | Events-only owner callback. Confirms the full outbound message matches the durable owner outbox, then validates active session relationship, writer, request/reply correlation, and current execution fences. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/owner-binding` | Events-only current session binding for message claim, presentation, and acknowledgment. Returns `Cache-Control: no-store`. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-owner-context` | Read-only mapped agent/model/turn context using the shared `RuntimeOwnerContext` contract in `Agentweaver.Abstractions`. Returns no-store metadata only if the child owner row and latest root decision still match, dispatch remains enabled for the confirmed item with no pending gate, and Projects authority/selection remain current; otherwise returns `409`. |
| `POST /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/fork-admission` | Events-only owner callback. Requires the forwarded authenticated actor and exact target/event/cursor/idempotency request to match a durable pending fork reservation, and revalidates the current accepted selection and execution fence. Returns a typed `SessionForkAdmissionReceipt` with `Cache-Control: no-store`; an absent, changed, stale, or terminal reservation returns `409`. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/policy-evaluations/{receiptId}` | Events-only read of the immutable Orchestrator-owned PolicyEvaluation receipt. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/policy-evaluations/{receiptId}/admission` | Events-only current admission check for that exact owner receipt. Every outcome requires current Core write authority and accepted selection, plus the matching actor/tenant and active owner session/run writer/fence. The owner rechecks authority after SQL row-lock waits, including duplicate receipt paths. Allow also requires the exact unexpired grant/fence; Deny/Error may reference an issued inactive grant but remain non-authorizing immutable facts. Returns no-store. |

Configuration:

| Key | Requirement |
| --- | --- |
| `ConnectionStrings:Orchestrator` | Password-free PostgreSQL connection with an Entra runtime role. |
| `Orchestrator:Database:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Required runtime workload identity. |
| `ConnectionStrings:OrchestratorMigration` | Separate connection with the migration Entra role; used only by `--migrate`. |
| `Orchestrator:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Required migration workload identity. |
| `Orchestrator:Schema` | Optional service-owned schema name; defaults to `orchestrator`. |
| `Identity:Issuer` / `Identity:Audience` | HTTPS issuer and incoming Orchestrator audience. |
| `ProjectsConfig:ProviderCatalog` | Optional catalog-owner snapshot used to validate the accepted Sandbox candidate. With no snapshot or no registered Sandbox resource adapter, plans needing isolation fail closed with `503`. |
| `ProjectsConfig:AuthorizationContext:OwnerBaseAddress` / `Audience` | Trusted HTTPS Projects & Config owner and required audience for current authority and selection. |
| `EventsAndSessions:Authorization:OwnerBaseAddress` / `Audience` | Trusted HTTPS Events & Sessions owner and required audience for session registration and message delivery. |
| `Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress` | Optional fixed HTTPS root for current Environment lease/profile lookup. Without this key, runtime registration routes are not mapped. |
| `Orchestrator:RuntimeUsage:BrokerOwnerAddress` | Optional fixed HTTPS Broker root for current observe-grant validation. Requires runtime registration configuration. |

Runtime PostgreSQL access uses the PostgreSQL Entra token scope and TLS
`VerifyFull`; the connection string must include the Entra role and omit a password.
Ordinary startup verifies the service schema. Run the executable with only
`--migrate` to apply migrations using the separate identity.
Coordination schema version 11 also applies immutable runtime registration,
SDK source, and observation receipt tables after the admitted session-tree and recovery migrations.
The runtime role does not apply this migration during ordinary startup.

## Events & Sessions journal

The `Agentweaver.EventsAndSessions` service is an unpublished .NET 10 host candidate.
Its protected session routes require an OpenIddict-validated bearer token with a GUID
`sub` and exactly one `project_id` and `run_id` pair issued for an active core grant.
The accepted-effect project-fact route separately checks the receipt's original
issuer, subject, project/run bounds, and fresh effective project permission. These
are service-internal contracts, not public product API routes; tenant and platform
role claims are not required.

| Method and path | Contract |
| --- | --- |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL and current owned-schema readiness; returns `503` when the schema is absent or outdated. |
| `POST /internal/sessions/{sessionId}` | Resolve and pin the run's native Sessions provider, then create the session. A run reuses its immutable provider binding; a different binding returns `409`. |
| `POST /internal/sessions/{sessionId}/events` | Append an ordinary versioned typed event to the project/run journal. Returns `403` for every generic `PolicyEvaluation` payload because actor equality does not establish trusted Core provenance. Ordinary appends return `201` for a new event, `200` for an identical run-scoped event-ID retry, and `409` if the ID is reused with different event content in that run. |
| `POST /internal/sessions/{sessionId}/fork` | Accept `SessionForkRequest` (`targetSessionId`, `sourceEventId`, `sourceCursor`, `idempotencyKey`). Requires a matching Orchestrator owner admission before journal work and immediately before transaction commit, including identical journal retries; validate that the cursor and event identify the same committed source event, then persist the target and immutable lineage. Existing object-reference retention deadlines are unchanged. Returns `201` for a new fork, `200` for an identical retry, `409` for missing/stale admission or conflicting event/cursor/target/key use, or `503` when the pinned Sessions provider does not support explicit forks. |
| `POST /internal/sessions/{sessionId}/policy-evaluations` | Append a PolicyEvaluation using a body containing only `receiptId`. Requires the pinned provider's `sessions.policy.evaluations` capability. Events fetches the immutable receipt from the fixed Orchestrator owner, validates current admission before writing, and repeats that check inside the native journal transaction before commit. Failed revalidation rolls back event, inbox, position, references, and outbox writes. Returns a no-store acknowledgment with receipt ID, session identity, event position, and duplicate status (`201` new, `200` identical retry). |
| `POST /internal/sessions/{sessionId}/usage-receipts` | Optional native usage route. Accepts only `receiptId`, fetches immutable Orchestrator evidence, and commits source hash, price, rate card, ledger, and inbox atomically. Returns a no-store accounting acknowledgment. |
| `GET /internal/projects/{projectId}/runs/{runId}/usage` | Optional native usage totals route. Requires current `ReadRunSelection` for the exact signed project/run and returns exact run and agent totals. |
| `GET /internal/sessions/{sessionId}/events?cursor={cursor}&limit={limit}` | Read an ordered page for one session after an optional opaque cursor. Positions are run-wide and may have gaps in a session-only page. |
| `GET /internal/sessions/{sessionId}/events/live?cursor={cursor}&maximumEvents={count}&maximumDurationSeconds={seconds}` | Poll durable journal state and stream NDJSON `SessionEventDelivery` records, each containing the event and a reconnectable `nextCursor`. |
| `GET /internal/projects/{projectId}/runs/{runId}/events?cursor={cursor}&limit={limit}` | Read a bounded, run-ordered page across all sessions in the authorized project/run. |
| `GET /internal/projects/{projectId}/runs/{runId}/events/live?cursor={cursor}&maximumEvents={count}&maximumDurationSeconds={seconds}` | Poll and stream run-ordered NDJSON deliveries across sessions; each delivery includes a reconnectable `nextCursor`. |
| `POST /internal/project-facts/accepted-effects` | Append a project-scoped accepted-effect fact. The body contains only `receiptId`, `schemaVersion`, and `eventVersion`. Events fetches the receipt from the fixed Knowledge owner, requires the original issuer/subject/resource bounds and current `WriteProjects`, then commits the fact, project sequence, and inbox receipt atomically. Returns the persisted acknowledgment for new and identical requests; changed reuse returns `409`. |

Project accepted-effect facts have their own address and sequence. They do not enter
`session_events`, use or change a run's Sessions-provider pin, or represent a
session/run transition. The durable acknowledgment binds the receipt and contract
versions to the native fact ID, project, and sequence.

The `Agentweaver.Abstractions` contract provides `ISessionsJournal`,
`SessionSubscriptionRequest`, `SessionIdentity`, `AppendSessionEvent`, and a
discriminated `SessionEventPayload`; `Agentweaver.EventsAndSessions` supplies the
PostgreSQL implementation. Event version 1 supports turns, tool calls,
accepted decisions and effects, artifact references, and cache references. Event
version 2 adds a typed `PolicyEvaluation` payload; existing version-1 payloads remain
appendable and replayable. The generic run-scoped append route rejects every
`PolicyEvaluation` payload because actor equality does not establish trusted Orchestrator
Core writer provenance. The dedicated receipt-reference route fetches immutable
Orchestrator evidence and requires current owner admission before and during its native
transaction. Every outcome requires fresh current Core authority, accepted selection,
matching actor/tenant, and the current owner session/run writer and fence. The owner
rechecks Core authority and accepted selection after SQL row-lock waits and before
returning admission success. Allow receipts additionally require an exact, active
grant/fence match; Deny/Error receipts may reference an issued inactive grant but remain
same-actor immutable evidence, not an allowance. The envelope binds project/run/session; the payload contains
bounded actor/tenant/step, grant reference/revision, purpose/action/fence,
outcome/reason, and provider/options identity metadata, with no arbitrary message or
credential fields. Other large payload content is represented by opaque
`ObjectKey` references; the journal does not store referenced bytes.

The run-level provider pin records provider ID, adapter version, options schema version
and revision, negotiated resource ID and generation, and capabilities. It is inserted
with the first session for a project/run and cannot be replaced by another create
request. Append, replay, and subscription verify the stored pin against the exact
registered provider and resource before proceeding; there is no provider fallback.
Run cursors are opaque and bound to the project/run; session cursors are additionally
bound to the session. Callers should return either token unchanged on replay or
reconnect.

The service also exposes protected internal addressed-message routes:

| Method and path | Contract |
| --- | --- |
| `POST /internal/addressed-messages/admit` | Accept an Orchestrator outbox message only after the Orchestrator validates the exact persisted message, current participants, request/reply correlation, and execution fences. Returns an admission receipt. |
| `POST /internal/addressed-messages/projects/{projectId}/runs/{runId}/sessions/{sessionId}/claim` | Claim the next eligible message only while the current Orchestrator session binding is `presenting`. |
| `POST /internal/addressed-messages/projects/{projectId}/runs/{runId}/sessions/{sessionId}/messages/{messageId}/present` | Mark the fenced claim as presented only while the current Orchestrator session binding is `presenting`. |
| `POST /internal/addressed-messages/projects/{projectId}/runs/{runId}/sessions/{sessionId}/messages/{messageId}/acknowledge` | Record receipt under the current session and claim fences. A valid correlated reply makes its request `input_available` on admission; this acknowledgment does not approve a gate or complete work. |

The store writes messages with idempotency scoped to the sender run and session, a
per-thread sequence, journal reference, and outbox record transactionally. Progress
messages remain in sender history and are not presented as recipient input. Reply
threads can be derived from the correlated reverse message. The store supports fenced
delivery-state operations. Owner relationship and accepted-selection checks are performed by the
Orchestrator through current Projects & Config authority. Orchestrator admission is
synchronous; these source additions do not include a background delivery relay, a
gate-approval decision, or automatic AgentHost scheduling. See [the owner integration
and persistence boundary](../architecture/events-sessions#addressed-message-owner-integration).

Configuration:

| Key | Requirement |
| --- | --- |
| `ConnectionStrings:EventsAndSessions` | PostgreSQL connection for the service-owned schema and transactional outbox/inbox. |
| `Identity:Issuer` | Absolute HTTPS OpenIddict issuer used to validate caller tokens. |
| `Identity:Audience` | Required token audience for the service. |
| `EventsAndSessions:Knowledge:BaseAddress` | Fixed absolute HTTPS Knowledge owner URI for committed accepted-effect receipts; redirects are rejected. |
| `ProjectsConfig:BaseAddress` | Fixed absolute HTTPS Projects & Config owner URI for fresh project authorization; redirects are rejected. |
| `EventsAndSessions:Provider:ResourceId` | Stable opaque resource identity used in the Sessions provider pin. |
| `EventsAndSessions:Provider:DatabaseName` | Expected live PostgreSQL database name; negotiation compares it with `current_database()`. |
| `EventsAndSessions:Provider:ResourceGeneration` | Positive generation recorded in the immutable provider pin. |
| `EventsAndSessions:Provider:Schema` | Optional owned schema name; defaults to `events_sessions`. |
| `EventsAndSessions:Provider:OptionsRevision` | Optional provider options revision; defaults to `native-postgres-v1`. |
| `EventsAndSessions:Provider:OptionsSchemaVersion` | Optional options schema version; defaults to `1`. |
| `EventsAndSessions:Provider:PollIntervalMilliseconds` | Optional live-poll interval from 50 to 30,000 ms. |
| `EventsAndSessions:Provider:ReferenceRetentionDays` | Optional object-reference retention from 1 to 3,650 days. |
| `EventsAndSessions:ProjectOverrides:{projectId}` | Optional project-level provider IDs permitted by the host catalog. |
| `EventsAndSessions:Messaging:OptionsRevision` | Optional native Messaging provider options revision; defaults to `native-postgres-messaging-v1`. |
| `EventsAndSessions:Messaging:OptionsSchemaVersion` | Optional Messaging options schema version; the current provider accepts only `1`. |
| `EventsAndSessions:Messaging:ClaimLeaseSeconds` | Optional claim lease from 10 to 3,600 seconds; defaults to `120`. |
| `EventsAndSessions:Messaging:MaximumMessageLifetimeDays` | Optional message lifetime from 1 to 7 days; defaults to `1`. |
| `ProjectsConfig:AuthorizationContext:OwnerBaseAddress` | Trusted HTTPS Projects owner base address for current authorization context and accepted run selection. |
| `ProjectsConfig:AuthorizationContext:Audience` | Audience required on the incoming token when the internal owner client is called. |
| `EventsAndSessions:Database:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Required explicit workload identity for the runtime PostgreSQL Entra role. |
| `ConnectionStrings:EventsAndSessionsMigration` | Separate PostgreSQL connection using the migration Entra role; required only by `--migrate`. |
| `EventsAndSessions:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Separate explicit workload identity used only by `--migrate`. |
| `EventsAndSessions:RuntimeUsage:Enabled` | Default-off reference-only SDK usage consumer and totals routes. |
| `EventsAndSessions:OrchestratorOwner:OwnerBaseAddress` / `Audience` | Fixed HTTPS Orchestrator owner and existing required audience for immutable source receipts. |

Both connection strings must omit passwords and name their Entra database role.
Connections use TLS `VerifyFull` and the PostgreSQL Entra token scope. The runtime
identity only verifies applied migrations and uses the already-created schema; it
cannot run DDL. Run the executable with only `--migrate` to apply embedded migrations
with the separate privileged identity. Ordinary startup verifies the service and
outbox schema and fails if a migration is pending. The service remains source-only: the repository does not include its deployment, a
Gateway route, automatic AgentHost scheduling, an active delivery relay,
or consistency-manifest workflow. See the
[Events & Sessions journal reference](../architecture/events-sessions).

For Knowledge promotion, the caller's existing bearer is forwarded when it is
audience-correct for both owners. If separate audience-correct tokens for the same
caller are already available, the caller may provide the Events token in
`X-Agentweaver-Events-Authorization`; Knowledge forwards its original bearer to
Events in a redacted request-only header so Events can fetch the receipt. Neither
token is acquired or persisted. The runtime does not provision these audiences.

## Identity broker endpoints

These routes belong to the unpublished Identity broker candidate. They are service contracts in source, not deployed product endpoints.

| Method and path | Contract |
| --- | --- |
| `GET /connect/authorize` | OpenIddict authorization request and external sign-in or consent prompt. |
| `GET /connect/authorize/resume` | Authenticated, single-use external sign-in continuation. |
| `POST /connect/consent` | Consent handle, approval, optional scope subset, local cookie, and `X-CSRF-TOKEN`. |
| `POST /connect/token` | Form-encoded authorization-code or refresh exchange. |
| `GET /diagnostics/whoami` | Bearer validation diagnostic. It is not an audience acceptance test. |
| `POST /secrets/redeem` | Validated bearer and exact secret ID, version, purpose, and run ID. |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL connectivity. |

The service has no secret-grant administration HTTP endpoint. The browser consent UI is not implemented.

Broker access tokens contain the local broker `sub`, registered OAuth scopes, and resource audience. They do not forward upstream tenant or role claims and do not assign Projects roles. Projects & Config is the sole live owner of issuer-and-subject project memberships and resource-role assignments. Downstream resource services obtain current effective permissions through its versioned owner contract rather than maintain duplicate membership or role records or caches; OAuth scopes and signed project/run bindings constrain requests but do not create authority.

### Runtime credential source candidate

Optional runtime bootstrap configuration enables a separate purpose-bound store.
These routes require the existing validated Broker audience and return
`Cache-Control: no-store`. A bearer or a configure body alone cannot prove a
runtime nonce. Current owner authority, exact registration, purpose, audience,
configuration hash, revision, expiry, and cryptographic verifier must match.

| Method and path | Contract |
| --- | --- |
| `POST /internal/runtime/bootstrap/request` | Current-registration delivery receipt; no credential in the receipt. |
| `POST /internal/runtime/bootstrap/verify-pending` | Pending nonce verification only; cannot consume or issue a source credential. |
| `POST /internal/runtime/bootstrap/consume` | CAS consumption receipt after completed Environment delivery. |
| `POST /internal/runtime/bootstrap/exchange` | Transient observe credential; exact replay returns the original receipt without a credential. |
| `POST /internal/runtime/source/verify` | Fresh current-registration and purpose-bound nonce verification. |
| `POST /internal/runtime/source/rotate` | New source revision and transient credential. |
| `POST /internal/runtime/source/revoke` | Immutable revocation receipt. |

Identity captures a verifier while the protected credential is live, before
database waits. Expiry during a grant-lock wait yields explicit denial and
durable revocation for the exact verified nonce. An already-expired input is
rejected before verification; that rejection does not prove durable cleanup.
Storage and Broker HTTP tests isolate the credential boundaries.
The combined local harness separately connects actual Core, Projects, Environment,
Orchestrator, SDK, and Events code.
External placement and SDK transport, catalog, and pricing inputs remain controlled.
This evidence is not cloud deployment or paid model acceptance.

The runtime registration candidate exposes these authenticated, no-store owner reads.
Registration alone does not authorize configure delivery or usage ingestion.
Identity verifies the separate purpose-bound nonce for those operations.

| Method and path | Source contract |
| --- | --- |
| `POST /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-registrations` | Accepts only `EnvironmentId` and `ProfileId`. Derives model, agent, turn, selection, fence, lease, provider, and endpoint pins from current owners. |
| `GET /internal/runtime/registrations/{runtimeInstanceId}` | Revalidates the active session/work item, accepted selection, current lease/profile, and registration revision. A raw storage read is not authorization. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/placement` | Public Environment control read. Requires current `WriteProjects`; returns the exact active, unexpired, owner-fenced lease projection. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/internal/placement` | Internal run-bound placement read. Uses existing current `ReadRunSelection` for the exact signed run. Does not grant public write permission. |
| `GET /internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}` | Uses the canonical manager's retained lease callback to read current Orchestrator context and match a registered profile to the exact placement. Requires current run-bound `ReadRunSelection`. |
| `POST /internal/runtime/sources/{runtimeInstanceId}` | Registers actual immutable SDK facts under the validated bearer and separate observe credential. Rechecks current owner and grant authority after waits. |
| `POST /internal/runtime/observations` | Commits a native SDK observation under the exact current registration/source grant. Identical SDK events return the original source receipt. Changed content conflicts. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/usage-receipts/{receiptId}` | Returns the immutable source receipt only after fresh accepted-selection and run-read checks. Events cannot substitute caller-supplied usage. |

`Environment:RuntimeBootstrap:Profiles` configures exact owner/profile/provider
registrations. An absent or mismatched registration returns explicit denial.
No caller supplies a configure URI or observation URI. Runtime bindings separately
pin `PlacementProviderId`, `EnvironmentLifecycleGeneration`, and
`EnvironmentLeaseRevision`; omitted legacy pins do not change stored binding JSON.
Lease expiry bounds registration expiry and cannot be extended by enrollment replay.

Current Projects policy deliberately withholds `WriteProjects` from run-bound tokens.
The public placement route still denies those tokens.
The internal read-only lookup uses existing `ReadRunSelection` authority and exact
current run/tenant binding. It reads no selection recursively and dispatches no provider effects.
The profile callback retains the lease transaction while it reads current work-item context.
Configure delivery still requires Identity's independently verified pending nonce.

The auth-first runtime library validates the configuration hash and exact configure
audience before consuming a delivered nonce. The current registration supplies the
accepted model reference. Native session creation uses an explicit session token
and the SDK's empty-mode policy; URI mode does not accept client-wide login options.
The SDK-reported effective model must match the registered selection. No ready session
is returned before the final registration, source-credential, and lifetime checks.
Controlled TCP tests exercise the actual SDK RPCs and usage callbacks.
The combined harness also commits source receipts and prices them through Events HTTP.
Only a source receipt ID crosses the accounting admission route.
No caller supplies a price or multiplies weighted nano-AIU again.
The ledger preserves explicit `Estimate`, `Reconciled`, and `Unpriced` dispositions.

`GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-owner-context`
requires the authenticated current run owner and returns `Cache-Control: no-store`.
The response contains the active child turn, accepted revisions and hash, and
agent/model reference from its confirmed WorkPlan item. An unknown, unmapped,
inactive, stale, or non-dispatchable child is unavailable. Caller configure JSON
cannot set these fields.

## Projects & Config authorization context

This route belongs to the unpublished Projects & Config candidate. It resolves only the validated caller's issuer and `sub`; it does not accept caller-subject or role selectors, issue grants, or mutate authority.

| Method and path | Contract |
| --- | --- |
| `GET /api/authorization/context` | Versioned current effective permission context, filtered by the validated audience, `api.read` scope, purpose, optional tenant selector, and project/run bindings. Returns `Cache-Control: no-store`. |

Contract version 1 contains the caller and selected tenant, current membership revision, optional project/run binding, and grouped effective permissions with their current role revisions. It omits assignment IDs and raw role rows. Purpose-bound tokens are denied; resource services must request fresh context for each privileged operation and must not cache or pin it.

## Knowledge and Memory

These routes belong to the unpublished `Agentweaver.Knowledge` .NET 10 service
candidate. Protected routes require the configured OpenIddict issuer and audience.
Each privileged request forwards its original validated bearer token to Projects &
Config for a fresh authorization-context check; Knowledge does not keep memberships,
roles, or authorization caches. Private content reads and writes require fresh effective
`WriteProjects` for the target project; `ReadProjects` alone is metadata-only and does
not authorize private Knowledge records, revisions, or context. Memory-provider
resolution additionally requires effective project `ReadRunSelection`. If the validated
caller token is already bound to a project/run, those bindings must match the requested
route and the authority response.

| Method and path | Contract |
| --- | --- |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL and current owned-schema readiness; returns `503` when migrations, tables, or required runtime grants are missing. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records` | Create a Memory, SessionContext, or Proposal record. Requires one `Idempotency-Key`; a new write returns `201`, an identical retry returns `200`, and reuse with different request content returns `409`. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records?kind={kind}&q={text}&includeInactive={bool}&page={n}&pageSize={n}` | Search only the requested project and agent, with bounded pages; requires current `WriteProjects` for private content. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}` | Read one record in the requested project/agent scope; requires current `WriteProjects` for private content. |
| `PUT /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}` | Append a revision using `expectedRevision` compare-and-swap and an `Idempotency-Key`; stale revisions return `409`. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}/revisions?page={n}&pageSize={n}` | Read bounded immutable revision history; requires current `WriteProjects` for private content. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/proposals/{proposalId}/promote` | Explicitly promote an owned pending proposal using its expected revision and an `Idempotency-Key`. The response includes `delivery` (`DELIVERED` or `PENDING`); pending delivery does not undo the committed promotion. An identical retry uses the same immutable receipt. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/proposals/{proposalId}/reject` | Explicitly reject a pending proposal using its expected revision and an `Idempotency-Key`. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/context?q={text}&maxItems={n}&maxTokens={n}` | Compose bounded context with immutable revision references; requires current `WriteProjects` for private content. Invalid narrowing is `400`; mandatory-content, candidate, or output budget overflow is returned explicitly as `413`. |
| `GET /internal/accepted-effects/{receiptId}` | No-store redacted accepted-effect receipt for the original issuer/subject and matching bounds, after a fresh current project `WriteProjects` check. Does not return proposal or decision content. |

The service owns a separate `knowledge` PostgreSQL schema. Revisions are append-only,
provider bindings are immutable, and current records cannot be physically deleted;
record changes use expected revisions. Proposal promotion checks current
`WriteProjects`, agent ownership, source run, pending state, and expected revision. It
commits the proposal revision, approved decision, redacted immutable receipt, and
Knowledge-owned outbox intent in one transaction. Events fetches the receipt and
appends a separate project fact; this is not a native Sessions journal event. Failed
delivery remains `PENDING` until a fresh authorized caller retries.

Configuration:

| Key | Requirement |
| --- | --- |
| `Identity:Issuer` | Absolute HTTPS issuer used to validate callers. |
| `Identity:Audience` | Required bearer-token audience for Knowledge. |
| `ProjectsConfig:BaseAddress` | Trusted absolute HTTPS Projects & Config owner URI; redirects are rejected. |
| `Knowledge:Events:BaseAddress` | Fixed absolute HTTPS Events owner URI for caller-driven project-fact delivery; redirects are rejected. |
| `Knowledge:Events:Audience` | Required Events audience for the already-issued caller bearer; a missing audience returns delivery as `PENDING` with the subject/audience tuple. |
| `ProjectsConfig:ProviderCatalog` | The catalog owner's startup snapshot used to validate negotiated provider metadata. |
| `ConnectionStrings:Knowledge` | Passwordless PostgreSQL runtime connection to the Knowledge-owned schema. |
| `Knowledge:Database:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Explicit projected workload identity for the runtime Entra database role. |
| `ConnectionStrings:KnowledgeMigration` | Separate connection for the explicit `--migrate` operation. |
| `Knowledge:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Separate explicit migration workload identity. |
| `Knowledge:Provider:{ResourceId,DatabaseName,ResourceGeneration,Schema,OptionsRevision,OptionsSchemaVersion}` | Expected resource identity and immutable provider options used during negotiation/pinning. |
| `Knowledge:Context:{MaximumCandidates,MaximumItems,MaximumTokens}` | Service-owned hard limits; each request can only narrow them and the current run's prompt-token limit. |

Both PostgreSQL connections omit passwords and use TLS `VerifyFull` with the
PostgreSQL Entra token scope. Ordinary startup verifies the already-applied schema and
requires the runtime identity to have only the necessary DML grants; it cannot migrate.
Run the executable with only `--migrate` to use the separate privileged identity. See
[Knowledge and Memory](../architecture/knowledge-memory) for ownership, context, and
current event-delivery boundaries.

## Identity host configuration

| Key | Requirement |
| --- | --- |
| `ConnectionStrings:IdentityBroker` | Identity-owned PostgreSQL database for runtime access. Use the separately bootstrapped Entra runtime role and omit a password; ordinary startup verifies but does not migrate. |
| `IdentityBroker:Issuer` | Absolute HTTPS issuer. |
| `IdentityBroker:Signing:PfxPath` | Mounted signing, encryption, and data-protection certificate. |
| `IdentityBroker:Signing:PfxPassword` | Deployment secret. Do not store it in source control. |
| `IdentityBroker:DataProtectionKeyPath` | Durable writable key-ring path shared by broker replicas. |
| `IdentityBroker:ExternalProvider:Authority` | Absolute HTTPS OIDC authority. |
| `IdentityBroker:ExternalProvider:ClientId` and `ClientSecret` | Registered upstream confidential client. |
| `IdentityBroker:Clients` | Registered client IDs, types, redirect URIs, scopes, resources, and secrets. |
| `IdentityBroker:SecretRedemption:Audience` | Required HTTPS audience registered as a client resource. |
| `IdentityBroker:SecretRedemption:VaultUri` | Azure Key Vault root URI. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId` | Explicit Entra tenant ID. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityClientId` | Explicit Entra client ID. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTokenFilePath` | Absolute projected token-file path. |
| `IdentityBroker:RuntimeBootstrap:OrchestratorOwnerAddress` / `EnvironmentOwnerAddress` | Optional composition; both fixed HTTPS root owner addresses are required when configured. |
| `IdentityBroker:RuntimeBootstrap:BootstrapLifetime` / `SourceLifetime` | Explicit positive lifetimes bounded by the current registration and actor expiry. |

The host does not use ambient credentials or a development-certificate fallback.

## Identity PostgreSQL access

Runtime and schema migration use separate connection strings, projected workload identities, and Entra PostgreSQL roles. Both connections use passwordless async Npgsql token acquisition, `VerifyFull`, and the scope `https://ossrdbms-aad.database.windows.net/.default`.

| Operation | Configuration | PostgreSQL role and boundary |
| --- | --- | --- |
| Ordinary runtime | `ConnectionStrings:IdentityBroker`; `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId`, `WorkloadIdentityClientId`, and `WorkloadIdentityTokenFilePath`. | The operator-selected runtime Entra role has `CONNECT`, schema `USAGE`, DML on current Identity/OpenIddict tables, and `SELECT` on the migration history. It does not own the schema or apply migrations. |
| Explicit migration | Run the executable with only `--migrate`; provide `ConnectionStrings:IdentityBrokerMigration` and `IdentityBroker:Migration:WorkloadIdentityTenantId`, `WorkloadIdentityClientId`, and `WorkloadIdentityTokenFilePath`. | A separate migration Entra role owns `identity_broker` and applies migrations. Its workload identity receives no Azure resource role. |

The database bootstrap and reviewed grant SQL are operator-run steps. The ordinary host checks that the schema exists and no migrations are pending; it fails rather than creating roles or changing the schema.
The four runtime grant tables have narrower masks: grant heads allow
`SELECT`, `INSERT`, and `UPDATE`; revisions, operations, and receipts allow
only `SELECT` and `INSERT`. Runtime deletion, audit updates, schema creation,
and migration-history writes remain forbidden.

## AKS Application Routing preview

`appRoutingDnsZoneResourceIds` is an optional deployment input. Empty input requests AKS's managed default domain. One to five unique existing public or private DNS zone IDs request custom domains and disable that default. IDs must belong to the exact authorized subscription, cannot name PrivateLink zones, and can use at most one resource group per zone kind.

The `appRoutingDomain` output has `managedDefaultRequested` and `domainName` fields. For an empty zone list, the template returns the exact `defaultDomain.domainName` from the AKS response. For custom zones, it returns `domainName: null`. The source does not construct hostnames or configure HTTP routes.

The `appRoutingIdentity` output is separate from `foundationProbeIdentity`. It contains the AKS-generated `resourceId`, `clientId`, and `objectId`.

| Role | Scope |
| --- | --- |
| Key Vault Certificate User (`db79e9a7-68ee-4b58-9aeb-b90e7c24fcba`) | The existing Key Vault resource. |
| DNS Zone Contributor (`befefa01-2a29-4197-83a8-272ff33ce314`) | Each exact configured public DNS zone. |
| Private DNS Zone Contributor (`b12aa53e-6015-4669-85d0-8515ebb3ae7f`) | Each exact configured private DNS zone. |

The AKS source uses the Preview `2026-07-02-preview` API, enables the Key Vault CSI provider, and sets secret rotation to `'true'` with a `'2m'` poll interval. These are source definitions. No live role assignment, addon activation, domain, certificate, or route was verified.

## Azure operator command

The supported tool reads the exact subscription and tenant supplied by the operator. Deployment requires separate approval for its target and cost.

Run a non-mutating summary with `node scripts/azure/deploy.mjs` and reviewed target arguments. Add `--execute` only after approval. The checked-in parameter examples contain placeholders.

## External acceptance evidence

The Foundation Probe Job reports its resource-operation results, but it does not attest to its own pod identity, pulled image, or exit status. The separate read-only consumer observes the completed Job and pod, checks their ownership and workload-identity projection, and verifies that both the Job image and observed pulled image match the expected registry manifest.

The consumer validates the native probe receipt against the admitted source SHA, Git tree, deployment, identity, and target. It then queries Azure Monitor only after Job completion and requires fresh `AppDependencies` or `AppRequests` evidence correlated by source SHA, Git tree, nonce, trace, and span. Configuration checks do not substitute for this runtime proof; incomplete or mismatched evidence remains blocked. Local fixtures validate the consumer contract but do not prove a live deployment.
