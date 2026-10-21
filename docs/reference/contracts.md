# Contracts, endpoints, and configuration reference

Use this reference to look up v1 service contracts, selected owner endpoints,
and their implementation boundaries. A route described here may be source-only
or conditionally mapped; read each section's status before treating a contract
as deployed.

For the public Gateway route inventory, see the [HTTP API reference](./api.md).
To configure a provider, follow [Configure providers](../guide/configure-providers.md).
For runtime settings and deployment configuration, see
[Runtime configuration](./runtime-configuration.md).

## Azure public routing

The managed Azure Gateway uses one HTTPS listener for the wildcard managed
domain. Broker, Web, Gateway API, and MCP HTTPRoutes attach to that listener
with exact hostnames and their existing path and backend rules. This lets a
Broker request remain routable when HTTP/2 reuses a Portal connection; an
unknown hostname still has no matching route. This topology does not change
the authentication or authorization contract of any service.

## Provider contract

`ProviderDescriptor` identifies a provider seam and records the provider ID,
adapter version, options-schema version, hosting pattern, and advertised
capabilities.

`ProviderRegistration` adds the enabled state, options revision, and selected
schema version. A `PinnedProviderBinding` records the run, resource generation,
and negotiated capabilities.

These records contain no credentials or option values. Follow
[Configure providers](../guide/configure-providers.md) to select and configure
a provider. See [Providers and models](../architecture/providers-models) for
resolver limits.

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

## Casting proposals and transfers

The Gateway exposes the existing Projects & Config casting contracts under
`/api/v1`. See the [API reference](./api.md#casting-through-the-gateway) for routes
and [Projects & Config](../architecture/projects-config.md#reviewed-casting-proposals-and-explicit-transfers)
for the owner transaction boundary.

| Contract | Fields and behavior |
| --- | --- |
| `CreateProjectCastingProposalRequest` | `expectedConfigurationRevision`, complete `draft`. |
| `CreateScenarioProjectCastingProposalRequest` | `expectedConfigurationRevision`, catalog `templateId`. |
| `CreateManualProjectCastingProposalRequest` | `expectedConfigurationRevision`, non-empty catalog `roleIds`. |
| `UpdateProjectCastingProposalRequest` | `expectedDraftRevision`, replacement `draft`. |
| `TransitionProjectCastingProposalRequest` | `expectedDraftRevision` for confirm or reject. |
| `ProjectCastingProposal` | `proposalId`, `projectId`, `baseConfigurationRevision`, `draftRevision`, `state` (`pending`, `confirmed`, or `rejected`), `draft`, nullable `confirmedConfigurationRevision`, nullable `result`, nullable `transferProvenance`, and creation/update actor IDs and timestamps. |
| `ProjectCastingTransfer` | `formatVersion` (1), `sourceProjectId`, `sourceConfigurationRevision`, `agentCharters`, `casting`, `contentDigest`. Contains no unrelated project settings. |
| `ProjectCastingTransferProvenance` | Verified transfer format, source project/revision, and content digest retained on an imported proposal. |
| `ImportProjectCastingTransferRequest` | Target `expectedConfigurationRevision` and `transfer`; creates a pending proposal. Request body limit is 1 MiB at Gateway and owner. |
| `ProjectCastingTemplateDefinition` | `id`, `title`, `description`, `roleIds`. |
| `ProjectCastingRoleDefinition` | `id`, `title`, `summary`, `responsibilities`, `boundaries`. |

The owner rejects unknown request fields and preserves revision conflicts as
`409`. The Gateway publishes these schemas in `GET /openapi/v1.json`, and the
web contract module supplies types only. There is no new casting permission,
idempotency header, or UI implementation.

## Cost and usage storage contracts

`CostBinding` records the meter source, provider identity, adapter version,
configuration revision, resource generation, negotiated capabilities, and immutable
`CostRateCard`. `PinCost` rejects changed candidate configuration. `VerifyCost`
rejects unavailable or changed provider identity. The adapter verifies the resource
generation and rate-card content.

`ICostProvider.Price` returns an amount, unit, rate card, and `CostDisposition`.
`Quote` requires an explicit provider-supported basis. The Copilot adapter prices
input, cached-input, cache-write, and output tokens from its immutable provider-
published rate-card snapshot, which records its source and retrieval date. Missing
rates or measurements return `Unpriced` with a reason and no amount. That status
does not block admission; unpriced usage remains visible and is excluded from
credit-limit totals. These methods do not authorize a model session or caller.
The standalone Azure Cost adapter uses an explicitly configured, versioned card
for model-scoped standard input/output token rates in the declared currency. It
does not fetch rates or quote BYOK work. Missing token counts, unsupported cache
categories, unknown models, and changed bindings remain `Unpriced`. Events must
verify trusted Azure provider and deployment facts against the accepted runtime
model pin before using this adapter for receipts. Until that source contract is
admitted, BYOK receipt accounting remains `Unpriced`; provisioned-throughput
usage also remains unpriced without trusted usage-share evidence.

## Gateway API and server-sent events

Project reporting uses two separate owner contracts. `ProjectMetrics` returns
current active and blocked counts, lifetime accepted runs, bounded created and
terminal-completed counts, succeeded runs, and zero-filled UTC daily points.
`ProjectUsage` returns ledger measurements grouped by model ID and agent ID.
The Gateway never aggregates across owners. Cross-project overview is out of
scope. See [Project metrics and usage](./api.md#project-metrics-and-usage) for
the field definitions and 0.x dashboard mapping.

Both contracts echo normalized `from` and `to` bounds. The half-open interval
defaults to the last 30 days and cannot exceed 92 days. Only metrics accepts
`bucket=day`. Both owners validate fresh project read authority, return
`Cache-Control: no-store`, and narrow run-bound tokens to their own run.
OpenAPI defines strict response objects and query schemas. Invalid or repeated
query values return a `400` problem response with `title` and `code` set to
`query_invalid`, without exception text.

Usage measurements remain nullable when source data is missing. Cost subtotals
are grouped by unit and disposition; consumers must not add different units
or treat unpriced usage as zero. The read path does not invoke pricing or
budgeting.

The `Agentweaver.Gateway` source exposes a finite, versioned client API at
`/api/v1`. The service is a source candidate; these mappings do not confirm a
deployment. `GET /openapi/v1.json` returns the route catalog and identifies each
owning service. Protected routes validate the Identity Broker access token for
the configured HTTPS issuer and Gateway audience, then forward that token and
the optional `X-Agentweaver-Tenant` selector to the owner. The Gateway does not
infer identity or roles from request data, accept arbitrary upstream URLs, or
replace the owner's current authorization checks.

Owner response statuses and bodies are preserved, including `409` conflicts and
`202 Accepted`. A `202` is an owner acceptance only; it does not prove that work
completed. Unavailable, redirected, or contract-invalid owner calls produce a
Gateway `502`; the finite owner timeout produces `504`. The live OpenAPI document
describes server-sent events (SSE) as `text/event-stream`; an invalid cursor
returns `400`.

| Method and path | Contract |
| --- | --- |
| `GET /openapi/v1.json` | Anonymous live OpenAPI 3.1 route and error discovery. |
| `GET /api/v1/authorization/context` | Fresh no-store Projects & Config context for the validated Broker bearer and optional tenant selector; no query parameters. The context is informational, not an authorization grant. |
| `GET /api/v1/projects/{projectId}/runs/{runId}/events` | Bounded replay from the committed Events journal. The owner remains responsible for current run authorization. |
| `GET /api/v1/projects/{projectId}/runs/{runId}/events/live` | SSE fan-out from committed Events pages. The Gateway rechecks exact run-bound `ReadProjects` authority before writing each event; each `id` is the Events journal cursor and is accepted on reconnect through `Last-Event-ID` or `cursor`. |
| Other `/api/v1` routes | Explicit routes in the live OpenAPI catalog delegate to Projects & Config, Orchestrator, Knowledge, or Events; no generic pass-through exists. |

The context route delegates to the existing Projects & Config
`GET /api/authorization/context` contract using the same validated bearer and,
when explicitly supplied, the owner-validated `X-Agentweaver-Tenant` selector.
The owner requires `api.read` and a non-purpose token. The response identifies
the actor, current tenant and membership revision, optional project/run bindings,
and effective authority; the Gateway validates the exact versioned JSON response
and marks it `Cache-Control: no-store`. This context does not authorize later
operations. The retained Web client forwards the selector only on routes
included in the allow list: Projects/configuration, run Coordination,
Knowledge, run Selection/Usage, and finite journal replay. Live SSE and
unrelated calls remain selector-free.
GitHub Repo App and ordinary Identity Broker browser
calls remain selector-free; Copilot user-connection BFF lifecycle calls forward
only the optional explicit tenant selector to their owning Broker.

The Gateway requires HTTPS `Identity:Issuer`, `Identity:Audience`, and HTTPS
service-root addresses for `Gateway:Owners:Projects`,
`Gateway:Owners:Orchestrator`, `Gateway:Owners:Knowledge`,
`Gateway:Owners:Events`, and `Gateway:Owners:IdentityBrokerAddress`. The
optional `Gateway:OwnerRequestTimeoutSeconds` ranges from 1 to 120 seconds and
defaults to 15. See [Runtime configuration](./runtime-configuration.md) for
deployment configuration guidance.

### GitHub Repo App and Copilot connection BFF

These explicit Gateway-to-Identity Broker routes are outside the OpenAPI catalog
and describe source mappings, not deployment availability. Repo App source
operations were released in #1934; Copilot operations require #1906. Missing
owner routes/configuration remain explicit failures.
User-level authorization, status, refresh/revoke, and repository discovery require
the current Broker bearer and no tenant selector. The status response exposes only
`connected`, `githubLogin`, and the stable opaque Identity `connectionId`; provider
tokens, numeric installation/repository IDs, and permissions remain owner-held.
Repository discovery returns safe metadata, while `POST /api/github/repository-selections`
accepts `{ "fullName": "owner/repository" }` and returns only a short-lived
single-use `{ "selectionCode", "expiresAt" }`.

| Method and path | Contract |
| --- | --- |
| `POST /api/auth/github/repo-app/authorizations` | Begin user OAuth with optional `returnRouteKey` for a route on the allow list; returns `authorizationUrl`, `transactionId`, and `expiresAt`. |
| `GET /api/auth/github/repo-app/authorization/status` | Read `{ connected, githubLogin, connectionId }`. |
| `GET /api/auth/github/repo-app/authorizations/{transactionId}` | Read the transaction's `{ status }`. |
| `POST /api/auth/github/repo-app/authorization/refresh` / `DELETE /api/auth/github/repo-app/authorization` | Refresh or revoke owner-held user authorization; no token payload. |
| `GET /api/github/repository-selections` | Read repository and installation display metadata without provider IDs or permissions. |
| `POST /api/github/repository-selections` | Exchange `{ fullName }` for an opaque short-lived `{ selectionCode, expiresAt }`. |
| `GET /auth/github/repo-app/callback` | Bearerless OAuth callback; only `code`, `state`, and `error` are forwarded with the exact transaction cookie. |
| `GET /auth/github/repo-app/installation/callback` | Bearerless installation callback; only `installation_id`, `setup_action`, and `state` are forwarded with the exact transaction cookie. |
| `POST /api/connections/copilot-user/v1/{begin,complete,refresh,revoke}` / `GET /api/connections/copilot-user/v1/{connectionId}` | Copilot user-connection lifecycle; forwards the unchanged bearer and optional explicit tenant selector, and complete forwards only the exact `__Host-agentweaver-copilot-link` cookie. |
| `POST /api/connections/remote-mcp/v1/register` / `GET /api/connections/remote-mcp/v1/{connectionId}` | Register a Remote MCP OAuth binding or read its redacted status; forwards the current bearer and optional explicit tenant selector. |
| `POST /api/connections/remote-mcp/v1/{connectionId}/consent` | Prepare metadata-checked consent and return the provider authorization URI. The current source has no project settings flow that starts this request. |
| `POST /api/connections/remote-mcp/v1/callback` | Resolve the pending connection from callback `state`, then forward either `{ state, code }` or `{ state, error: "access_denied" }` with the current bearer; token values are never returned to Web. |
| `POST /api/connections/remote-mcp/v1/{connectionId}/refresh` | Refresh an authorized connection using expected connection, credential, and configuration revisions; an uncertain provider result returns `503 remote_mcp_oauth_refresh_uncertain`, becomes indeterminate, and cannot be retried blindly. |
| `POST /api/connections/remote-mcp/v1/{connectionId}/disconnect` | Clear Identity's protected token references after current binding and revision checks; provider revocation is not yet wired. |

The OAuth and installation callbacks preserve the owner's `Location` and every
`Set-Cookie` header. Their host-only cookies are
`__Host-agentweaver-repo-app-auth` and
`__Host-agentweaver-repo-app-install-auth`; the browser does not forward an
Identity Broker session cookie. The Copilot begin flow uses only
`__Host-agentweaver-copilot-link`. No Gateway token exchange or caller-supplied
actor/project/redirect authority is added.

For accepted project configuration, optional `sourceControl.authMode` is
`secret` or `githubApp`; older configurations that omit it remain legacy secret
mode without rewriting omitted defaults. `sourceControl.appConnectionId` is the
stable Identity reference for GitHub App mode, and `apiSecretReference` remains
for secret mode only. The existing run-bound
`POST /api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/pin`
accepts an optional `{ "selectionCode": "..." }` body for App mode; omitting the
body preserves legacy behavior. Both modes keep the existing
`SourceControlRepositoryPinView` response and `200`/`202` semantics. The owner
matches the opaque code against the accepted repository and stores exact provider
IDs server-side.

The browser lifecycle, user-level selection APIs, and callbacks are not exposed
as first-party MCP tools. Run-bound repository operations in the finite OpenAPI
catalog remain available as ordinary MCP tools and require `tenantSelector`.
Run-produced files are available through the Source Control output-capture
operations: capture, list, read, diff, and file. They read pinned, immutable
captures; no raw filesystem Gateway proxy is part of this contract.

## First-party MCP client

`Agentweaver.Mcp` provides a Model Context Protocol (MCP) Streamable HTTP
endpoint at `POST /mcp`.
`tools/list` reflects only compatible operations and schemas from the served
Gateway OpenAPI catalog; `tools/call` maps to those fixed `/api/v1` Gateway routes.
It does not accept an owner URL, identity, role, or authorization decision from
tool arguments.

| Route | Contract |
| --- | --- |
| `POST /mcp` | Native MCP initialize, tool discovery, and tool calls. Requires an OpenIddict-validated Broker bearer for the MCP audience. |
| `GET /.well-known/oauth-protected-resource[/{audiencePath}]` | Public RFC 9728 metadata derived from the configured resource audience; identifies that resource and the Broker issuer. |

An unauthenticated MCP request returns `401` with a Bearer
`resource_metadata` challenge. The MCP host validates issuer, signature, expiry,
and audience before forwarding the unchanged bearer to Gateway. Gateway performs
its own audience validation and dispatches the fixed route to the current owner;
owner statuses and response bodies are preserved. HTTP failures and owner
`accepted: false` results become MCP tool errors, with the owner response retained.
An owner `202 Accepted` is not completion evidence.

Tool arguments are checked against the same resolved OpenAPI schemas returned by
`tools/list` before the Gateway is called. Catalog retrieval and tool calls have a
10-second deadline covering response bodies as well as headers; caller cancellation
is propagated, and timeout or Gateway unavailability is surfaced as an MCP error.

The MCP host requires HTTPS `Identity:Issuer`, `Identity:Audience`, and
`Gateway:BaseAddress` service-root settings. Its tool catalog is fetched from
the configured Gateway; callers cannot override this address. See
[Runtime configuration](./runtime-configuration.md) for deployment settings.

## Environment remote MCP connections

The Environment source candidate stores owner-authorized remote MCP connection
configuration and immutable catalog pins. Its routes use current Projects &
Config authority and no-store responses. The exact route list, revision fields,
Identity correlation boundary, parser limits, and missing discovery/transport
proof are described in [Remote MCP connections](../architecture/remote-mcp-connections.md).
See [Runtime configuration](./runtime-configuration.md) for runtime settings.

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
Optional `A2AMessageId` and `SdkAccounting` bind native provenance to the canonical payload.
Null metadata does not change historical canonical hashes.
Hosted `nano_aiu` pricing does not require optional accounting identity or credit status.
A supplied identity must have valid text, a positive sequence, and the same SDK session.
The exact hosted source, meter, and measured units remain required.
An unpriced entry keeps run and agent pricing incomplete.
BYOK token pricing remains separate from hosted credit status.

`RuntimeUsageCostSnapshotRequest` carries the admitted registration and actual SDK source.
`POST /internal/projects/{projectId}/runs/{runId}/usage/copilot-cost-snapshot` returns
the source hash, existing run-pinned Cost binding, zero-work quote, and Copilot-only totals.
Events checks the current signed run authority before committing the pin.
The quote uses the actual model and `ProviderWeightedNanoAiu` basis.
It writes no synthetic usage entry, source receipt, or accounting acknowledgment.
The same Cost lock protects pricing resolution and the observed totals read.
Missing pricing remains `Unpriced`; an empty ledger has a zero priced subtotal without
turning an unpriced quote into a priced zero. Snapshot validation accepts explicit
unpriced Copilot quotes and usage, retains their reasons and event counts, and returns
only the known priced subtotal. It rejects other meters and non-AIC units.
These observed totals do not prove future accounting-source finality.
An optional `DispatchId` and up to 512 `RequiredReceipts` request an observed accounting join.
References must have unique source and event IDs with exact runtime attribution.
Hosted-Copilot snapshots also require run and per-agent totals to include every
represented receipt; native-turn accounting uses the same check.
`RepresentedReceipts` contains the matching immutable source and ledger receipts.
Events checks actual registration, source, platform message, accounting hash, and price under the same transaction.
Optional `NativeTurn` carries the expected recorded native receipt; it is omitted from historical requests.
Events verifies it through `POST /internal/runtime/turns/recorded`, not through caller claims.
That authenticated Core read returns the canonical persisted observation only for the current source,
registration, owner checkpoint, report hash, and recorded revision.
Its transformed native event IDs must equal the required accounting event set.
Events reads Core before its Cost transaction and again after releasing the Cost lock.
Only verified native evidence permits a null raw platform message ID.
Wrong non-null IDs, late events, changed evidence, missing rows, and altered prices still fail.
`ValidateObservedReceipt` allows explicit unpriced accounting.
The separate priced validator still rejects unavailable pricing for configured hosted credit admission.

`RuntimeUsageSourceReceipt` contains the immutable runtime registration, native
usage submission, source hash, receipt ID, version, and recorded timestamp.
`RuntimeUsageSourceReceiptContract` validates exact owner, SDK, model, catalog,
event, turn, and accepted-selection pins. It rejects changed hashes and Copilot units on BYOK sources.
This receipt proves a source observation, not a price or accounting acknowledgment.
A stored source receipt remains readable after its original lease expires.
That historical read does not authorize another observation.

`RuntimeNativeTurnAdmissionReceipt` binds a stable platform message to its current
registration, SDK source, request hash, prompt hash, and committed owner revision.
It contains no predicted native message or turn ID.
`MaxModelTurns` reserves whole-run send capacity when that existing MAF dispatch is prepared.
Exact replay consumes no new slot; sending or uncertain work cannot be resent or refunded.
This is not a count of internal SDK model API calls.
`RuntimeNativeTurnBeginResult` contains exactly one admission or
`RuntimeNativeCreditDenialReceipt`. A hard-limit denial binds the current
registration, source, dispatch, checkpoint, measured credits, ledger digest and
count, Cost identity, and before/after guard and dispatch revisions. Orchestrator
stores it in the existing outbox with the credit-guard update, without a schema
migration. `POST /internal/runtime/turns/denials/read` returns the stored denial
for the exact dispatch and stage under current source authorization.
At preparation, the existing budget error remains after the denial commits.
At native begin, AgentHost receives the typed denial while the dispatch remains
`prepared` at the same owner revision.
For hard-capped Hosted Copilot, AgentHost captures the native event-log cursor
before begin. After a denial, it accepts only a complete bounded range with zero
events and unchanged cursor hashes. `RuntimeNativeTurnNoSendReceipt` binds that
range to the denial, registration, source, and SDK session hash. The Host submits
it through `POST /internal/runtime/turns/no-send`; Orchestrator verifies the
persisted denial and unchanged prepared dispatch and stores the evidence in the
existing outbox. Only after that receipt is stored does the Host return the
budget error. Any event, incomplete range, or changed cursor fails closed; the
Host does not call native Send. This evidence does not prove later usage
accounting is complete.
`RuntimeNativeTurnObservation` carries actual native IDs, the durable completion range,
output hash, observed root usage-event IDs, and an optional accounting checkpoint.
`RuntimeNativeTurnRecordedReceipt` binds that observation hash to the admitted intent.
The source writer records it as partial, not source-complete.
`RuntimeNativeTurnAccountingRequest` carries the recorded native proof and exactly its observed usage references.
`POST /internal/runtime/turns/accounting` verifies the existing Events join before completing that dispatch.
Core checks the committed report, releases owner locks for the Events request, and
rechecks current authority and the same report in its final completion transaction.
Report insertion, proof reads, and final completion reject event reuse by another dispatch of the same SDK source.
`RuntimeNativeTurnAccountingReceipt` binds the observation, represented references, and newer owner revision.
The source report stays partial; no source-finality or strict retirement proof is written.
The Host returns the actual answer after this durable observed completion.
MAF verifies real pricing before preparing capped hosted work and repeats it at native begin.
After the response, MAF rereads the completed dispatch and exact output hash before retaining a result.
Uncapped unpriced output remains available; later usage affects the next capped boundary.

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
durable Events & Sessions receipt acknowledgment before the protected callback. Events
accepts only the immutable receipt reference. It revalidates current owner admission
inside the journal transaction and returns a no-store acknowledgment. Downstream
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

Reviewed remote tool contracts retain immutable snapshot data, exact call identity,
canonical arguments, and matching result metadata. The internal snapshot store checks
the full stored snapshot digest and rejects conflicting reuse of an ID. Its embedded
018 SQL resource is tested directly but is not registered in the normal migration path.
The accepted Projects configuration can include exact `reviewedRemoteToolSnapshots`
references. Orchestrator resolves these references against the immutable store and
checks the stored digest, project, and exact reference. It refreshes Projects authority
after resolution and rejects missing, changed, duplicate, or foreign references.
These checks do not verify a current remote connection or per-call grant.
They do not authorize credential use or native network requests.

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
| `GET /api/projects/{projectId}/runs` | List accepted runs in the project, newest first, as a `ProjectRunPage`. The caller needs `api.read`, both Orchestrator and Projects audiences, and current project or tenant read permission from Projects & Config. A run-bound token sees only its own run. The keyset cursor is opaque and bound to the query. A state filter is applied to each run's derived state, so a selective filter can read many older runs before it fills a page. Each `ProjectRunSummary` reports the run ID, root session ID, derived state, timestamps, `completedAt`, and the accepting actor as `startedBy`. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/root` | Accept the root session for the current accepted run selection and register it with Events & Sessions. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/decisions` | Read the current typed decision state and pending gate without exposing a transferable authorization or provider pin. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/propose_outcome_spec` | Propose a schema-validated outcome specification. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/select_workflow` | Select a workflow from the authorized catalog or submit a generated definition for confirmation. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/propose_work_plan` | Validate a plan against the selected workflow, current role/model eligibility, and the accepted Sandbox binding before opening its confirmation gate. |
| `POST /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/pin` | Legacy secret mode accepts the existing bodyless request and redeems its API SecretRef. GitHub App mode accepts only `{"selectionCode":"<64 lowercase hex>"}`; it consumes the code for the initial run-bound pin and persists only its hash, never the code in project/run configuration or the pin response. First pin returns `202` with `SourceControlRepositoryPinView`; an exact existing pin returns `200`. Both paths recheck fresh Projects/Core authority. |
| `POST /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/workspaces/{workspaceId}/output-captures` | Capture the existing bound workspace using its base-SHA/branch binding while the run is active, idle, or blocked. Source Control persists the sealed package with its pending proof. A retry for that workspace reuses the exact package without reading the live checkout; changed capture inputs conflict until the pending entry is resolved. A new capture returns `201`; an exact idempotent replay returns `200`. Admission follows the exact Events & Sessions journal acknowledgment and fresh authority/run-state checks. |
| `GET /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/output-captures` | List admitted captures after current read-authority and accepted-selection checks. `limit` defaults to 50 and is bounded to 100. Continue with both `beforeCapturedAt` and `beforeCaptureId` from the preceding page; the pair must be supplied together. |
| `GET /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/output-captures/{captureId}` | Return the admitted capture summary and canonical manifest. Pending or unadmitted records are not exposed. |
| `GET /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/output-captures/{captureId}/diff` | Return the verified captured patch for this admitted capture. |
| `GET /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/output-captures/{captureId}/files?path={relativePath}` | Return one manifest-listed file as `application/octet-stream` and its SHA-256 in `X-Source-Control-Output-Sha256`. The path is a safe relative workspace path, not a host path. The Gateway serves the same five output-capture routes under `/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}` with a required `X-Agentweaver-Tenant` header, and the MCP host exposes them as `agentweaver_` tools. |
| `POST /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/merge-intents` | Read current PR facts from the pinned provider, persist an immutable merge request, then open typed Approval for that exact intent and accepted Merge step. |
| `GET /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/merge-intents/{intentId}` | Read an owner-bound merge intent without exposing credential values or transferable authority. |
| `POST /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/merge-intents/{intentId}/execute` | Execute only through the current source-specific grant and existing action guard, under a repository-scoped lock and exact-head/check preflight. The expected base is a fresh preflight, not an atomic compare-and-swap. |
| `POST /api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/webhook-relay` | Accept raw GitHub bytes only from an authenticated run-bound relay; verify the pinned webhook SecretRef, repository identity, current authority, and durable delivery id. |
| `POST /api/source-control/github/webhook` | Reject direct GitHub delivery. No trusted unauthenticated-to-run relay identity is deployed in this source slice. |
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
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/suspend` | Accept `{ "idempotencyKey": "..." }`. Until a real authenticated AgentHost drain/checkpoint path can produce the required cache, journal, workspace, and network evidence, persist an `interrupted` operation/manifest and return `503`; do not claim suspension or a new Core fence. An exact retry replays the recorded outcome. If the 019 schema extension is not active, return an explicit schema-unavailable `503` without claiming a persisted operation. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/resume` | Accept `{ "idempotencyKey": "...", "manifestId": "..." }`. Validate the manifest's run/session scope, canonical hash, and fence, then fail closed with a persisted `interrupted` operation/manifest and `503` until a real restore, current egress verification, and fenced dispatch path exists. If the 019 schema extension is not active, return an explicit schema-unavailable `503` without claiming a persisted operation. |
| `POST /internal/runtime/suspend/require-current` | Authenticated host control path. Accept the exact `RuntimeHostSuspendRequest` and return that same request only when its proof's current registration binding and Projects authority match the persisted suspend operation, reserved manifest ID, fence, `fenced` phase, and positive phase version. The check does not require or create a terminal manifest before capture. Workspace data generation can be zero, but a completed suspended manifest still requires an exact tree hash or provider checkpoint. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/notifications?limit={limit}` | Read unacknowledged parent notifications; the limit defaults to 50 and is bounded to 100. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/notifications/{notificationId}/acknowledge` | Acknowledge a notification belonging to this parent session. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/messages/{messageId}/acknowledge` | Acknowledge a delivered message using its claim fence; receipt is not gate approval or work completion. |

Output capture is a bounded sealed-tree snapshot, not an atomic filesystem snapshot. Its canonical
manifest binds safe relative paths, Git modes, per-file SHA-256 digests, and lengths; the package and
diff have independent digests. Limits are 10,000 files, 16 MiB per file, 64 MiB total file content,
32 MiB for the manifest, and 8 MiB for the diff. Source Control records the proof as pending before
Events & Sessions stores the package bytes in Object Store and appends the typed journal event. Only
the exact journal event ID and position acknowledged by Events can admit the immutable owner record.
The event ID is derived by the owner; callers cannot provide provenance. Reads recheck current
Projects authority and accepted selection, verify the journal-backed package and file digests, and
do not fall back to the live workspace. Completed-run history remains available; failed or
indeterminate runs and pending captures return not found. These owner-backed history routes can
support a retained UI without making a UI part of this contract.
| `POST /internal/projects/{projectId}/runs/{runId}/coordination/message-route` | Events-only owner callback. Confirms the full outbound message matches the durable owner outbox, then validates active session relationship, writer, request/reply correlation, and current execution fences. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/owner-binding` | Events-only current session binding for message claim, presentation, and acknowledgment. Returns `Cache-Control: no-store`. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-owner-context` | Read-only mapped agent/model/turn context using the shared `RuntimeOwnerContext` contract in `Agentweaver.Abstractions`. Returns no-store metadata only if the child owner row and latest root decision still match, dispatch remains enabled for the confirmed item with no pending gate, and Projects authority/selection remain current; otherwise returns `409`. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-execution-observation` | Read-only current child checkpoint observation using the `RuntimeExecutionObservation` contract. It returns only that child’s status, pending message/prompt hashes, and completed-result digest/UTF-8 length—never result text. It rechecks the active owner, decision, mapping, and Projects selection after the checkpoint read; stale state returns `409`. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-execution-observation/v2` | Uses the same read-only authorization and stale-state checks. Its version-2 wrapper contains the unchanged validated v1 `observation` and a `checkpointParent` object with required `hasParentMetadata` and nullable `parentCheckpointId` fields. True/null means a verified root; false/null means unknown legacy metadata. Contradictory or invalid saved parent data returns `409`; the v1 route and response remain unchanged. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/build-test/commands/{checkpointId}/{stepId}` | Reads the immutable accepted BuildTest command metadata. Requires `workPlanId`, `checkpointRevision`, `decisionStateVersion`, `executionFence`, and `acceptedSelectionHash` query values plus current owner and Projects authorization. Version 2 includes the bound workspace-source metadata, never binary seed bytes. Rechecks current selection, decision, fence, and command after store reads. Returns no-store; stale references return `409`. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/build-test/commands/{checkpointId}/{stepId}/workspace-seed` | Uses the same complete reference and authorization to read the original version-2 seed through the existing checkpoint store. Rechecks current authority and command after hydration. Returns bounded raw `application/octet-stream` bytes with no-store, not JSON or a caller-selected URL. Version 1 returns `404`; stale references return `409`. Environment verifies the exact accepted length and SHA-256 digest before execution. |
| `POST /api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/build-test/commands/{checkpointId}/{stepId}/workspace-capture/verify` | Checks the exact native capture reference, collector Pod, and journal acknowledgment against the current accepted command, original seed, and canonical admitted Source Control row. Requires a matching object reference and positive journal position. Rechecks current command and source-owner authority after reads. Returns the verified `ProducedRunCaptureJournalEntry` with no-store. A caller-supplied acknowledgment alone is not admission. |
| `POST /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/fork-admission` | Events-only owner callback. Requires the forwarded authenticated actor and exact target/event/cursor/idempotency request to match a durable pending fork reservation, and revalidates the current accepted selection and execution fence. Returns a typed `SessionForkAdmissionReceipt` with `Cache-Control: no-store`; an absent, changed, stale, or terminal reservation returns `409`. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/policy-evaluations/{receiptId}` | Events-only read of the immutable Orchestrator-owned PolicyEvaluation receipt. |
| `GET /api/projects/{projectId}/runs/{runId}/coordination/policy-evaluations/{receiptId}/admission` | Events-only current admission check for that exact owner receipt. Every outcome requires current Core write authority and accepted selection, plus the matching actor/tenant and active owner session/run writer/fence. The owner rechecks authority after SQL row-lock waits, including duplicate receipt paths. Allow also requires the exact unexpired grant/fence; Deny/Error can reference an issued inactive grant but remain non-authorizing immutable facts. Returns no-store. |

For complete runtime-setting guidance, see
[Runtime configuration](./runtime-configuration.md).

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

For complete runtime-setting guidance, see
[Runtime configuration](./runtime-configuration.md).

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
| `GET /internal/projects/{projectId}/runs/{runId}/sessions-provider-binding` | Returns the actual immutable `SessionProviderBinding`, not an accepted candidate. Requires existing current `ReadRunSelection` and exact signed project/run binding before and after verified retrieval. No option values or credentials. Returns no-store. |
| `POST /internal/sessions/{sessionId}/events` | Append an ordinary versioned typed event to the project/run journal. Returns `403` for every generic `PolicyEvaluation` payload because actor equality does not establish trusted Core provenance. Ordinary appends return `201` for a new event, `200` for an identical run-scoped event-ID retry, and `409` if the ID is reused with different event content in that run. |
| `POST /internal/sessions/{sessionId}/fork` | Accept `SessionForkRequest` (`targetSessionId`, `sourceEventId`, `sourceCursor`, `idempotencyKey`). Requires a matching Orchestrator owner admission before journal work and immediately before transaction commit, including identical journal retries; validate that the cursor and event identify the same committed source event, then persist the target and immutable lineage. Existing object-reference retention deadlines are unchanged. Returns `201` for a new fork, `200` for an identical retry, `409` for missing/stale admission or conflicting event/cursor/target/key use, or `503` when the pinned Sessions provider does not support explicit forks. |
| `POST /internal/sessions/{sessionId}/policy-evaluations` | Append a PolicyEvaluation using a body containing only `receiptId`. Requires the pinned provider's `sessions.policy.evaluations` capability. Events fetches the immutable receipt from the fixed Orchestrator owner, validates current admission before writing, and repeats that check inside the native journal transaction before commit. Failed revalidation rolls back event, inbox, position, references, and outbox writes. Returns a no-store acknowledgment with receipt ID, session identity, event position, and duplicate status (`201` new, `200` identical retry). |
| `POST /internal/sessions/{sessionId}/usage-receipts` | Optional native usage route. Accepts only `receiptId`, fetches immutable Orchestrator evidence, and commits source hash, price, rate card, ledger, and inbox atomically. Returns a no-store accounting acknowledgment. |
| `POST /internal/sessions/{sessionId}/material` | Optional typed `SessionMaterialWriteRequest` route. Accepts actual `TurnContent` or `SdkCache` bytes, at most 1 MiB, with event ID, runtime ID, registration revision, and execution fence. Requires the original bearer, current Projects authority, recorded SDK source, and exact current runtime binding. Stores bytes before the journal reference. Rechecks authority after waits and on retries. Returns the original no-store acknowledgment for identical material, `409` for changed event reuse, or an explicit authority/storage error. |
| `GET /internal/sessions/{sessionId}/material/{eventId}/{kind}` | Reads only committed material by session, event, and kind. `TurnContent` requires current `ReadRunSelection` or actually supplied `ReadProjects` entitlement, exact signed project/run binding, and recorded session membership. Project-summary access alone does not authorize opaque bytes. `SdkCache` retains Core `ReadRunSelection` authority. The route verifies the recorded Sessions provider, digest, length, kind, and version, then rechecks read authority after retrieval. Historical content does not require dispatchability, a live lease, or model credentials. Returns a no-store `SessionMaterialReadResult`. Arbitrary object keys, paths, and URLs are not accepted. |
| `GET /internal/projects/{projectId}/runs/{runId}/usage` | Optional native usage totals route. Requires current `ReadRunSelection` for the exact signed project/run and returns exact run and agent totals. |
| `POST /internal/projects/{projectId}/runs/{runId}/usage/copilot-run-admission` | Optional run-start pricing route. Accepts contract version 1 and only the accepted-selection hash. Requires current `ReadRunSelection` and `AcceptRunSelection`, rereads the trusted Projects selection, and resolves the shared versioned AgentHost model map. Returns a no-store receipt with the concrete model/connection, the Cost pin when available, an explicit quote, and Copilot-only totals. Missing rates remain `Unpriced` and do not block root acceptance or dispatch; only the known priced subtotal counts toward credit limits. Malformed or tampered receipts fail closed, and no SDK or usage evidence is invented. |
| `POST /internal/projects/{projectId}/runs/{runId}/usage/copilot-cost-snapshot` | Optional observed pricing route. Requires current run authority and exact immutable source and ledger references. Native references require matching authenticated Core proof reads before and after the Cost transaction. Returns no-store; unavailable or changed evidence fails. |
| `GET /internal/sessions/{sessionId}/events?cursor={cursor}&limit={limit}` | Read an ordered page for one session after an optional opaque cursor. Positions are run-wide and can have gaps in a session-only page. |
| `GET /internal/sessions/{sessionId}/events/live?cursor={cursor}&maximumEvents={count}&maximumDurationSeconds={seconds}` | Poll durable journal state and stream NDJSON `SessionEventDelivery` records, each containing the event and a reconnectable `nextCursor`. |
| `GET /internal/projects/{projectId}/runs/{runId}/events?cursor={cursor}&limit={limit}` | Read a bounded, run-ordered page across all sessions in the authorized project/run. |
| `GET /internal/projects/{projectId}/runs/{runId}/events/live?cursor={cursor}&maximumEvents={count}&maximumDurationSeconds={seconds}` | Poll and stream run-ordered NDJSON deliveries across sessions; each delivery includes a reconnectable `nextCursor`. |
| `POST /internal/project-facts/accepted-effects` | Append a project-scoped accepted-effect fact. The body contains `receiptId`, `schemaVersion`, and `eventVersion`, plus either both `projectId` and `runId` (current scoped request) or neither (the original three-field N-1 request); a partial scope returns `400`. Scoped requests read only from the already-pinned Memory provider. The legacy unscoped request reads from the native PostgreSQL Knowledge route. Events fetches the receipt from the fixed Knowledge owner, requires the original issuer/subject/resource bounds and current `WriteProjects`, then commits the fact, project sequence, and inbox receipt atomically. Neither route falls back to the other. Returns the persisted acknowledgment for new and identical requests; changed reuse returns `409`. |

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
grant/fence match; Deny/Error receipts can reference an issued inactive grant but remain
same-actor immutable evidence, not an allowance. The envelope binds project/run/session. The payload contains bounded
actor/tenant/step, grant reference/revision, purpose/action/fence,
outcome/reason, and provider/options identity metadata. It has no arbitrary
message or credential fields. Other large payload content is represented by opaque
`ObjectKey` references; the journal does not store referenced bytes.

The run-level provider pin records provider ID, adapter version, options schema version
and revision, negotiated resource ID and generation, and capabilities. It is inserted
with the first session for a project/run and cannot be replaced by another create
request. Append, replay, and subscription verify the stored pin against the exact
registered provider and resource before proceeding; there is no provider fallback.
Run cursors are opaque and bound to the project/run; session cursors are additionally
bound to the session. Callers should return either token unchanged on replay or
reconnect.

### Private reviewed tool result material

`RemoteToolResultProof` binds the full reviewed call, snapshot, actor and tenant,
accepted revisions, registration and fence, and native action admission.
`RemoteToolResultVerificationReceipt` contains the derived result event and exact proof digest.
These values are not caller-granted authority.

| Method and path | Contract |
| --- | --- |
| `POST /internal/runtime/remote-tool-results/verify` | Orchestrator checks the configured native mutual-TLS peer before reading the proof. It reconstructs the current selected call and verifies native action admission and the reserved tool slot. |
| `POST /internal/sessions/{sessionId}/remote-tool-results` | Events accepts typed `RemoteToolResult` material only from its configured Orchestrator TLS peer, with the original current actor. It stores bytes before the journal reference and returns a fully bound `SessionMaterialAcknowledgment`. |
| `POST /internal/sessions/{sessionId}/remote-tool-results/read` | Events accepts a call proof, verifies the committed result and current authority, and returns bounded bytes with their acknowledgment. Missing recorded material returns `404`; changed material or authority is not treated as missing. |

All handled responses use `no-store`.
Generic material routes and public event routes do not grant private result access.
The shared receipt validator requires exact event, identity, runtime binding, proof,
source, length, and SHA-256.
A read template can adopt only the recorded content class; all other proof fields remain exact.
See the [TLS configuration and unproven acceptance boundary](../architecture/events-sessions#private-result-tls-configuration).

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
| `EventsAndSessions:SessionMaterial:ContainerUri` | Optional explicit HTTPS Blob container for typed material. Enables the material routes with the Events & Sessions workload identity, which receives Storage Blob Data Contributor on the private `session-material` container only. User information, query parameters, fragments, and nested container paths are rejected. |
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
audience-correct for both owners. If separate audience-correct tokens for the
same caller are already available, the caller can provide the Events token in
`X-Agentweaver-Events-Authorization`. Knowledge forwards its original bearer to
Events in a redacted request-only header. Events uses that bearer to fetch the
receipt. Neither token is acquired or persisted. The runtime does not provision
these audiences.

## Identity broker endpoints

These routes belong to the unpublished Identity broker candidate. They are service contracts in source, not deployed product endpoints.

| Method and path | Contract |
| --- | --- |
| `GET /` | Redirect to `/.well-known/openid-configuration` with HTTP 302. |
| `GET /connect/authorize` | OpenIddict authorization request and external sign-in or consent prompt. Supports `prompt=none`: it never displays sign-in or consent UI, and returns an authorization code only when the same client already has valid consent for the requested scopes. |
| `GET /connect/authorize/resume` | Single-use external sign-in continuation; preserves `prompt=none` after successful upstream sign-in and returns silent interaction errors to the registered redirect URI. |
| `POST /connect/consent` | Consent handle, approval, optional scope subset, local cookie, and `X-CSRF-TOKEN`. |
| `POST /connect/token` | Form-encoded authorization-code or refresh exchange. |
| `GET /diagnostics/whoami` | Bearer validation diagnostic. It is not an audience acceptance test. |
| `POST /secrets/redeem` | Validated bearer and exact secret ID, version, purpose, and run ID. |
| `POST /internal/connections/remote-mcp/register` | Bind the authenticated human, tenant, project, current Environment connection revision/resource, configured issuer/client/callback, and operator-approved scopes. |
| `GET /internal/connections/remote-mcp/{connectionId}` | Read owner-authorized, no-store status without exposing token references or values. |
| `POST /internal/connections/remote-mcp/{connectionId}/consent` | Link the exact Environment revision, validate current provider client, redirect, resource, and scopes plus issuer metadata, persist a short-lived state/PKCE correlation and protected verifier reference, then return an authorization URI. This route does not redirect the browser. |
| `POST /internal/connections/remote-mcp/callback` | Resolve the pending connection from the hashed callback state for the current owner; accept either the state and authorization code or the state and `access_denied`, recheck current provider approvals, owner, and Environment binding, claim once, exchange only an authorization code, and persist only protected token `SecretRef`s. |
| `POST /internal/connections/remote-mcp/{connectionId}/refresh` | Claim the exact authorized credential revision, redeem its protected refresh-token reference, recheck current authority and Environment binding, and persist rotated token references with compare-and-swap. Stale claims and uncertain provider outcomes become indeterminate and are not retried. |
| `POST /internal/connections/remote-mcp/{connectionId}/disconnect` | Revision-check and clear local credential references; provider revocation is not yet wired. |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL connectivity. |
| `GET /auth/github/repo-app/csrf` | Return no-store `{"csrf_token":"…"}` and set the antiforgery cookie for local-cookie Repo App routes. JSON POSTs send the token in `X-CSRF-TOKEN` with credentials included; same-origin form POSTs use the default `__RequestVerificationToken` field. |
| `POST /auth/github/repo-app/connect` | Require the local Identity cookie and antiforgery token, create owner-bound PKCE state, set the callback cookie, and redirect to GitHub OAuth. |
| `POST /auth/github/repo-app/install` | Require the local Identity cookie and antiforgery token, then start an owner-bound GitHub App installation callback. |
| `GET /auth/github/repo-app/callback?state=…&code=…[&installation_id=…][&setup_action=…]` | Complete the single-use user OAuth or installation callback using the local owner, state, and callback cookie; clear the cookie and redirect to the settings page on success. Responses are no-store. |
| `GET /auth/github/repo-app/status` | Return no-store status `{state,localReadiness,connectionId,connectionRevision,githubLogin,accessTokenExpiresAt,updatedAt}`. `state`: `not_connected`, `connected`, `revoked`, `rotation_uncertain`; `localReadiness`: `not_connected`, `access_token_available`, `refresh_required`, `refresh_in_progress`, `reauthorization_required`, `rotation_uncertain`. Status reads persisted state and does not probe GitHub. |
| `POST /auth/github/repo-app/disconnect` | Require local-cookie owner and antiforgery; body `{"connectionId":"…","expectedConnectionRevision":1}`. Returns the status DTO after revoking the local connection and cached installation records; it does not uninstall the GitHub App remotely. |
| `GET /auth/github/repo-app/repositories` | Discover installations and repositories available to the connected GitHub user; no-store response `{connectionId,connectionRevision,githubLogin,repositories:[{installationId,repositoryId,fullName,ownerLogin,isPrivate,defaultBranch}]}`. |
| `POST /auth/github/repo-app/selection` | Require local-cookie owner and antiforgery; body `{"installationId":123,"repositoryId":456}`. Return no-store `{code,connectionId,connectionRevision,installationId,repositoryId,repositoryFullName}` with a short-lived, one-time code for that exact repository. |
| `POST /internal/source-control/github-app/installations/token` | Validate the run-bound Broker bearer and active grant, recheck current connection, installation, repository, selection and permission binding, and return a no-store ephemeral exact-repository token to Orchestrator. Its permission digest is derived from GitHub's actual response; `IssueWriteGranted` is true only when the response confirms `issues:write`. A requested but ungranted `issues:write` fails closed; a 422 never causes a narrower retry, and remints preserve the accepted scope/digest. |

`connectionRevision`, `installationId`, and `repositoryId` are positive signed Int64
values; the selection route rejects nonpositive installation and repository IDs.
`connectionId` is a string. The durable Source Control pin exposes `pinId`,
`repository`, `providerId`, `resourceId`, `resourceGeneration`,
`providerRepositoryId`, `defaultBranch`, `isPrivate`, and `pinnedAt`; it does not
expose the selection code or any credential value.

The service has no secret-grant administration HTTP endpoint. The browser consent
experience belongs to Web; these routes provide its broker protocol.

For a valid `prompt=none` request, the Broker forwards `prompt=none` to the
external identity provider when it needs to establish a local session. It maps
upstream `login_required`, `consent_required`, and `interaction_required`
responses from query or form-post callbacks to those same OpenID Connect error
codes. A missing or revoked
authorization returns `consent_required`. These errors go only to the
registered redirect URI and include the original `state`; silent authorization
does not create or extend a permanent authorization.

`IdentityBroker:WebOrigin` is an optional, origin-only HTTPS value, required when
`IdentityBroker:GitHubRepoApp` is configured. It has no path, credentials, query, or
fragment. Identity normalizes it and allows credentialed CORS only for the browser
authorize, resume, consent, token, and Repo App fetch routes, with `GET`/`POST` and
the `Content-Type`/`X-CSRF-TOKEN` headers. CORS runs before authentication so valid
preflights do not require a session; the endpoints still enforce their existing
authentication, owner, and antiforgery checks. Internal redemption, installation-token,
runtime, diagnostics, and health routes do not receive this CORS policy.

The Repo App provider callback remains an absolute Broker-origin URI. After a valid
callback, Identity redirects to the fixed absolute Web completion path
`/settings/source-control?repoApp=connected`; it does not accept a caller-supplied
return URL. Broker cookies remain host-only, Secure, HttpOnly, and SameSite=Lax. The
Web and Broker origins must remain within the same schemeful site for these cookies;
CORS does not override SameSite.

The Repo App routes are mapped only when the optional `IdentityBroker:GitHubRepoApp`
configuration is present. The local-cookie routes do not constitute a settings UI;
the token-mint route is an internal source boundary, not a public GitHub endpoint.

Broker access tokens contain the local broker `sub`, registered OAuth scopes, and resource audience. They do not forward upstream tenant or role claims and do not assign Projects roles. Projects & Config is the sole live owner of issuer-and-subject project memberships and resource-role assignments. Downstream resource services obtain current effective permissions from the versioned owner contract. They do not maintain duplicate membership or role records or caches. OAuth scopes and signed project/run bindings constrain requests but do not create authority.

### Runtime credential source candidate

Optional runtime bootstrap configuration enables a separate purpose-bound store.
These routes require the existing validated Broker audience and return
`Cache-Control: no-store`. A bearer or a configure body alone cannot prove a
runtime nonce. Current owner authority, exact registration, purpose, audience,
configuration hash, revision, expiry, and cryptographic verifier must match.

These source contracts can deliver a model credential to the current guest
AgentHost; they do not implement credential-less execution. The
[proposed contract changes](../architecture/identity-secrets.md#minimal-contract-changes-and-admission)
bind an explicit mode and supported gateway/runtime profile to existing
registration, action, provider and ready-check surfaces. In that proposed mode,
raw redemption/exchange routes accept trusted external consumers only; a guest
gateway proof cannot redeem an upstream credential. The proposed L7 operation
endpoint is not a currently mapped Broker or Gateway/BFF route.

| Method and path | Contract |
| --- | --- |
| `POST /internal/runtime/bootstrap/request` | Current-registration delivery receipt; no credential in the receipt. |
| `POST /internal/runtime/bootstrap/verify-pending` | Pending nonce verification only; cannot consume or issue a source credential. |
| `POST /internal/runtime/bootstrap/consume` | CAS consumption receipt after completed Environment delivery. |
| `POST /internal/runtime/bootstrap/exchange` | Transient observe credential; exact replay returns the original receipt without a credential. |
| `POST /internal/runtime/source/verify` | Fresh current-registration and purpose-bound nonce verification. |
| `POST /internal/runtime/source/rotate` | New source revision and transient credential. |
| `POST /internal/runtime/source/revoke` | Immutable revocation receipt. |
| `POST /internal/runtime/model-session/grant` | Current-registration model grant with source mode, credential kind, exact secret version, and hosted connection revision. |
| `POST /internal/runtime/model-session/verify` | Revalidates the complete receipt against current Identity and runtime authority. |
| `POST /internal/runtime/model-session/redeem` | Transient credential plus typed current receipt. Hosted responses contain only the user access token, never the refresh envelope or OAuth client secret. |

### Copilot user connections

The Identity-owned source candidate supports these authenticated, no-store routes.
Begin, completion, refresh, and revocation require fresh, unbound Core authority for the exact project or platform scope.
Status requires the same owner and current Core authority.

Receipts contain `connectionId`, `revision`, `scope`, `scopeId`, `state`, and `freshUntil`.
The `scope` values are `project` and `platform`.
The `state` values are `pending`, `connected`, `refreshing`, `transientUnavailable`, `reconnectRequired`, `revoked`, and `refreshIndeterminate`.
Begin returns `{ connection, authorizationUri }`; its callback cookie never appears in JSON.
Unknown request members deny.

| Method and path | Contract |
| --- | --- |
| `POST /internal/connections/copilot-user/begin` | `{ scope, scopeId }`. Returns a pending connection receipt and PKCE authorization URI. Sets the secure browser nonce cookie. |
| `POST /internal/connections/copilot-user/complete` | `{ state, code?, error? }` plus the same authenticated subject and browser nonce. Claims single-use state before the GitHub exchange. |
| `GET /internal/connections/copilot-user/{connectionId}` | Returns connection identity, revision, scope, state, and freshness. Contains no credential values. |
| `POST /internal/connections/copilot-user/refresh` | `{ connectionId, expectedRevision }`. Claims the current revision before upstream rotation and conditionally publishes an exact protected-store version. |
| `POST /internal/connections/copilot-user/revoke` | `{ connectionId, expectedRevision }`. Retains the connection and immutable audit history while denying future runtime use. |

The callback requires `__Host-agentweaver-copilot-link`.
Identity sets `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, and a five-minute lifetime without a `Domain`.
The frontend callback requires the current bearer and forwards only this nonce through its authenticated BFF completion request.
The callback does not exchange codes anonymously, store bearer values in state,
or persist credentials in PostgreSQL.
Gateway routing and browser surfaces remain separate source slices.

Malformed successful OAuth or user responses return an explicit contract error and leave the connection in `refreshIndeterminate`.
The committed credential version remains unchanged, and the same connection cannot repeat the uncertain exchange.

`IdentityBroker:CopilotConnection` requires explicit `ProjectsOwnerAddress`, GitHub `ClientId`,
HTTPS `CallbackUri`, and exact `ClientSecretReference`.
The callback URI must use the fixed browser return path `/auth/github/copilot-app/callback`.
Its origin is explicit operator configuration, not a default host or evidence of live GitHub registration.
The same URI is used for GitHub authorization and code exchange.
The separate UI bridge uses a same-origin popup and the opener's existing in-memory authentication context.
It validates the popup source, origin, and expected state before authenticated completion through Gateway with the actual nonce cookie.
The return page has no anonymous credential or mutation authority and persists no browser tokens.
The protected-store workload identity requires the separate writer's SET and exact-version GET permissions.
This configuration does not change the read-only P0 Secrets principal or provision cloud permissions.

The routes remain mapped when this optional configuration is absent.
They return `503` with `copilot_connection_writer_unavailable`, not a fabricated connection or a route-not-found response.
Fresh owner denials return `403` with the exact `copilot_connection_*` error.
Invalid input returns `400` with `copilot_connection_request_invalid`.
Protected-store or upstream outages return explicit `503` errors.

Known refresh rejection produces `ReconnectRequired`.
A known transient response produces `TransientUnavailable` and permits explicit retry with its new revision.
An uncertain upstream rotation, store write, or publication produces `RefreshIndeterminate` and prohibits blind replay.
Failed publication retains the previously committed exact reference.
GitHub installation tokens cannot become Copilot user credentials.

### AgentHost runtime routes

The unpublished [AgentHost executable](../architecture/agenthost.md) validates a Broker bearer and the exact HTTPS configure audience.
It compares the current immutable registration, purpose-bound grant, image, lease, execution fence, and model proof after owner waits.
All runtime responses are no-store.

| Method and path | Contract |
| --- | --- |
| `POST /runtime/v1/configure` | `RuntimeBootstrapDeliveryRequest`. Delivers the pending configure nonce without activating a model session. |
| `POST /runtime/v1/configure/activate` | `RuntimeHostConfigureRequest`: version 1, exact registration, consume/exchange operation IDs, and delivered configuration. Exact replay returns the existing immutable session. Workflow-bound Hosted Copilot supports a hard credit limit through prepared-dispatch and native-begin admission. An unbound positive hard limit still fails with `runtime_usage_affinity_unavailable`; the prepared session is cleaned up and readiness is withheld. |
| `POST /runtime/v1/refresh` | `RuntimeHostRefreshRequest`: exact `RuntimeHostSessionProof` and operation ID. Current Identity and owner authority control rotation and replay. |
| `GET /runtime/v1/readiness` | Returns the existing noncredential `RuntimeHostReadinessReceipt` after it revalidates the caller, current registration, session, Observe grant, and readiness evidence. Ordinary configuration includes source-derived recovery mode and evidence bound to the current Host instance; NativeCache reports the exact source acknowledgment, while JournalRebuild reports the authenticated journal position/hash. It does not start an SDK session or send a turn. |
| `POST /runtime/v1/a2a/message:send` | `RuntimeA2ASendRequest`: one user text message, GUID message ID, native context ID, exact runtime proof, and delivery mode. `immediate` precedes pending `enqueue` work only at the next native idle boundary. A hard-cap denial returns only after the Host records the complete empty native range as source-owned no-send evidence. |
| `GET /health/live` | Process liveness. It does not prove permission or readiness. |
| `GET /health/ready` | Fresh registration/source/lease/isolation/workspace/egress evidence plus authenticated configuration and measured startup ceilings. Missing evidence returns `503`. |

The A2A response contains `kind`, `messageId`, `contextId`, `role`, and actual assistant text `parts`.
Completion follows committed turn content, Policy evidence, and usage accounting.
The routes do not implement a scheduler, background relay, or gate approval from message receipt.

### Runtime credential verification

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
| `POST /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-registrations` | Accepts only `EnvironmentId` and `ProfileId`. Derives model, optional exact model `SecretRef`, agent, turn, selection, fence, lease, provider, and endpoint pins from current owners. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-registrations` | Requires the current authenticated child owner. It reads no more than 64 scoped registration IDs in pages of 32, revalidates each against current authority, and returns one current match. It returns `409` for no match, ambiguity, or candidate-limit overflow. |
| `GET /internal/runtime/registrations/{runtimeInstanceId}` | Revalidates the active session/work item, accepted selection, current lease/profile, and registration revision. A raw storage read is not authorization. |
| `GET /internal/runtime/registrations/{runtimeInstanceId}/skills` | Revalidates the current registration and accepted run selection, then returns only the enabled skill revisions assigned to its active agent, with exact instructions, resources, digests, and registration binding. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/placement` | Public Environment control read. Requires current `WriteProjects`; returns the exact active, unexpired, owner-fenced lease projection. |
| `GET /api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/internal/placement` | Internal run-bound placement read. Uses existing current `ReadRunSelection` for the exact signed run. Its additive `providerPin` records the actual successful consumer's provider, adapter/options revisions, resource generation, and negotiated capabilities, without option values or recovery metadata. Does not grant public write permission. |
| `GET /internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}` | Uses the canonical manager's retained lease callback to read current Orchestrator context and match a registered profile to the exact placement. Requires current run-bound `ReadRunSelection`. |
| `POST /internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}/resolve` | Accepts `RuntimeBootstrapProfileResolveRequest` version 1. Reads the current owner-fenced Sandbox placement and owned Service/Pod endpoint, plus the receiver configure URL and audience from immutable AgentHost configuration. Verifies a no-store JSON `/health/live` response over HTTPS at the observed Pod IP using receiver DNS for TLS, with a 10-second deadline and 4 KiB body limit; sends no credentials and does not reuse a connection across endpoint checks. Persists an endpoint-bound replacement profile and returns `RuntimeBootstrapProfileResolveReceipt` with its profile ID. The replacement ID pins the requested source profile, authenticated actor, and accepted selection hash; the stored record also pins source and replacement profile configuration hashes. Commit replay and bootstrap/readiness/Workspace consumption recheck these pins and the exact copied profile against the current source configuration; changed identity or configuration fails closed, including after application reload. The base profile and accepted model, tool, and provider selections are unchanged. Repeated resolution for the same placement is stable; a changed Pod or Service yields a different profile. Requires current run-bound `ReadRunSelection`. |
| `GET /internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}/workspace` | Returns `EnvironmentRuntimeWorkspaceContext` with the current placement, exact validated Workspace mount negotiation, and attached transition revision. One owner lock protects both lease and Workspace during the Core context read. Requires current run-bound `ReadRunSelection`. This is metadata, not flush or content evidence. |
| `POST /internal/runtime/sources/{runtimeInstanceId}` | Registers actual immutable SDK facts under the validated bearer and separate observe credential. Rechecks current owner and grant authority after waits. |
| `POST /internal/runtime/observations` | Commits a native SDK observation, including optional A2A message correlation, under the exact current registration/source grant. Identical SDK events return the original source receipt. Changed content conflicts. |
| `GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/usage-receipts/{receiptId}` | Returns the immutable source receipt only after fresh accepted-selection and run-read checks. Events cannot substitute caller-supplied usage. |

The staged Environment resume contract uses these authenticated, no-store routes.

| Method and path | Source contract |
| --- | --- |
| `POST /internal/runtime/resume/prepare` | `RuntimeWorkspaceResumeRequest` contains the saved manifest and `RuntimeSuspendPlacementReleaseRequest`. `RestoredWorkspace` must be absent. Returns `RuntimeWorkspaceResumePlacementReceipt` for the exact new mount; this is not content verification. |
| `POST /internal/runtime/resume/verify` | The same request requires Core's persisted `RestoredWorkspace` and current phase version. Returns `RuntimeWorkspaceResumeVerificationReceipt` only after actual Host tree-hash and fresh owner, placement, and Network checks. |
| `POST /internal/runtime/resume/require-current` | Core confirms the exact source manifest, saved producer receipt, resume operation, and phase. With `RestoredWorkspace`, it also confirms Core's saved complete mount response. Environment and Host retain the original actor. |
| `POST /runtime/v1/workspace/resume` | Read-only Host verifier. Returns `RuntimeHostWorkspaceResumeReceipt` bound to the operation, source manifest, exact new Workspace context, and actual tree hash. It does not configure a model session or recover cache files. |
| `POST /internal/runtime/resume/bootstrap` | Environment accepts Core's phase-4 `RuntimeResumeBootstrapRequest`. Returns `RuntimeResumeBootstrapOutcome` with exactly one complete receipt or explicit state-loss proof. It persists successful attempt receipts and freshly rechecks the Host on replay. |
| `POST /internal/runtime/resume/require-bootstrap-current` | Core confirms the exact phase-4 operation, saved mount, current Runtime registration, and server-issued attempt. A caller-selected attempt is not authority. |
| `POST /internal/runtime/resume/configuration` | Events reads the exact acknowledged server configuration. Native TLS, current Core authority, registration, private material identity, and byte hash are required. Generic and public material reads cannot expose it. |
| `POST /runtime/v1/configure/resume-state` | The Host returns `RuntimeResumeHostStateReceipt` for the exact operation, manifest, registration, and attempt. Valid states are `BootstrapPresent`, `Activated`, and `Missing`; an omitted or unknown state is rejected. |

The request and both Environment clients belong to `Agentweaver.Identity`.
Resume validates the source's private configuration acknowledgment before owner calls or Environment effects.
Ordinary suspend receipt validation remains unchanged; missing configuration cannot authorize resume.
Prepare and verify use Core phase versions 2 and 3.
The target lifecycle is the source plus one; Storage resource and data generations do not change.
The final Network generation is the source plus one, not a default readiness counter.
The optional bootstrap `runtimeEndpoint` contains the observed HTTPS Service address, Pod address, Pod UID, and Service UID.
The transport pins the observed Pod address and uses ordinary TLS verification for the owned Service name.
The complete target context is compared before and after native verification.
`RuntimeBinding.ResumeOperationId` and `ResumeAttemptId` are paired optional fields.
Ordinary bindings omit both, so their existing serialized identity does not change.
Resume bootstrap requires the matching attempt and canonical bootstrap, consume, and exchange IDs.
A replacement derives from the exact revoked prior registration under the same operation.
Core must serialize selection and revocation; an already-issued active attempt survives a lost response.
The immutable registration revision stores the attempt without a same-phase update to migration 019 progress.
`RuntimeResumeBootstrapStateLossReceipt` requires a matching delivery receipt and fresh Host `Missing` proof.
The shared bootstrap client returns a valid receipt or throws `RuntimeResumeBootstrapStateLostException` with that validated proof.
Generic denials, timeouts, historical delivery, and saved Host receipts alone cannot authorize replacement or dispatch.
`RuntimeHostResumeCacheReceipt` preserves the original source cache acknowledgment in both recovery modes.
`native-cache` requires the compatible saved cache facts and forbids `JournalRebuild`.
`journal-rebuild` requires `RuntimeResumeJournalRebuildReceipt` with the exact saved checkpoint, flushed position, digest, and active attempt.
Its only supported native-cache state is `PendingUntilAuthorizedTurn`; it contains no new cache acknowledgment.
The current Host source facts do not need to match the old cache's SDK or Runtime version.
The shared digest covers the ordered authenticated session-visible prefix, including valid run-wide position gaps.
The producer must verify saved-boundary pagination, material bytes, current authority, and the saved source configuration and cache references.
Each TURN must match its own journal payload and single stored object reference, and its material read must match that same reference and event position.
The recorded runtime, registration revision, fence, selection, SDK, model, and
capacity facts belong to that TURN. They need not match the latest cache or
current Runtime.
The source-session replay response supplies the saved fork visibility. A direct material read does not make another session visible.
A receipt hash alone is not independent proof of those reads.
Missing required journal evidence blocks readiness; owner, transport, configuration, hash, and fence failures cannot trigger another-source fallback.
The next real authorized turn uses the existing native cache commit path.
Suspend before genuine native files exist fails explicitly without a cache success receipt.
These source contracts do not prove native TLS, PostgreSQL, mounted contents, or deployed dispatch.

`Environment:RuntimeBootstrap:Profiles` configures exact owner/profile/provider
registrations. An absent or mismatched registration returns explicit denial.
No caller supplies a configure URI or observation URI. Runtime bindings separately
pin `PlacementProviderId`, `EnvironmentLifecycleGeneration`, and
`EnvironmentLeaseRevision`; omitted legacy pins do not change stored binding JSON.
Lease expiry bounds registration expiry and cannot be extended by enrollment replay.
A resumed placement retains that original profile and saved resume source.
Its new configure address is resolved from the currently verified Kubernetes Service and Pod, not another configured placement.

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

`RuntimeActionRequest` optionally carries `DispatchId` for `model.turn` and `IsToolInvocation` for registered pre-tool hooks.
A configured model-turn cap requires the exact prepared dispatch, runtime binding, and prompt hash.
The default false invocation flag and null dispatch ID stay omitted from historical payloads and hashes.
After current Policy allows a tool invocation, Orchestrator reserves its event ID and request hash in the existing outbox.
Reservations use the run-level `runtime-tool-invocations` ledger stream, separate from
session message streams, so retained reservations do not block addressed-message delivery.
`MaxToolCalls` counts those reservations across the whole run, including after store restart.
Exact HTTP retry reuses the event ID; a new invocation cannot reuse an arguments hash as identity.
Permission callbacks do not debit another slot.
Verify requires the retained exact reservation before dispatch.

The accepted project `MaxPromptTokens` value is preserved in `RuntimeOwnerContext`
and the runtime binding; it is not silently discarded or reclassified as a
Knowledge retrieval budget. When configured, AgentHost resolves the effective
prompt capacity from the accepted limit and the concrete model capacity,
including any lower server limit, then passes it to the native SDK as a
compaction threshold. For BYOK, the server-owned model binding supplies the
concrete capacity; unavailable capacity rejects activation with
`runtime_model_prompt_capacity_unavailable`. The pinned SDK compacts removable
history. For an irreducible latest turn, it may send a provider request during
compaction and then report a capacity-attributed `query` error. This is not a
hard admission check for every internal provider request or a no-send guarantee,
and input is not silently truncated.

Optional `SdkSessionFacts.MaxPromptTokens` records the effective native SDK
compaction threshold, not aggregate token usage, a per-request admission guarantee,
or a raw-request token count. The session-material
owner binds this source fact and rejects a changed value. Absent optional pins
preserve historical payloads; they do not authorize a configured-capacity resume.
Native and hosted deployment evidence is still required to close issue
[#1855](https://github.com/sabbour/agentweaver/issues/1855).

`GET /internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-owner-context`
requires the authenticated current run owner and returns `Cache-Control: no-store`.
The response contains the active child turn, accepted revisions and hash, and
agent/model reference from its confirmed WorkPlan item.
For a Fixed workflow step, the agent, model reference, and step come from the
running association in the root's owner-created `coordinator-execution`
checkpoint. The association must match the active child and current accepted
selection, execution fence, and decision version.
The accepted platform or project selection also supplies `ModelSourceMode`.
Runtime registration and model-grant receipts retain that mode.
Missing or incompatible modes deny SDK startup without a personal provider fallback.
Hosted selections carry a stable connection ID and its accepted project or platform scope.
Identity resolves the current connection revision and exact secret version for each model-session grant.
BYOK selections carry an exact `SecretRef` only when the WorkPlan model matches the accepted model.
Neither selection field is a grant or credential value. An unknown, unmapped,
inactive, stale, or non-dispatchable child is unavailable. Caller configure JSON
cannot set these fields.

Identity extracts only the current user access token from its protected hosted envelope before the Host receives a response.
BYOK sessions supply explicit SDK provider configuration and skip Copilot authentication and catalog lookup.
Hosted receipts bind the current connection identity, revision, scope, credential kind, freshness, and exact secret version.
Every asynchronous redemption and SDK preparation boundary revalidates that proof.
Rotation changes the credential revision, not the immutable accepted selection.
Controlled native transport evidence does not prove GitHub entitlement or deployed connection services.
BYOK usage retains token measurements and rejects Copilot nano-AIU attribution.
BYOK selections also retain the server-owned `ModelBindingPin`, including its provider type and configuration hash.
The Host compares this pin before SDK use. A missing or changed pin denies the session without a hosted fallback.
`SdkSessionFacts.ByokProvider` records the actual provider type, effective deployment ID, and accepted configuration hash.
The deployment ID must equal both source and usage model IDs.
Core rejects missing input/output measurements, changed provider facts, and Copilot units.
The controlled Azure fixture proves a dispatched native usage event through Broker, Core PostgreSQL, and reference-only Events accounting.
This proof does not authorize paid Azure calls.
Without an admitted Cost provider, its accounting remains `Unpriced`, not zero-cost or hard-bound admission proof.

## Projects & Config authorization context

This route belongs to the unpublished Projects & Config candidate. It resolves only the validated caller's issuer and `sub`; it does not accept caller-subject or role selectors, issue grants, or mutate authority.

| Method and path | Contract |
| --- | --- |
| `GET /api/authorization/context` | Versioned current effective permission context, filtered by the validated audience, `api.read` scope, purpose, optional tenant selector, and project/run bindings. Returns `Cache-Control: no-store`. |
| `GET /api/projects/{projectId}?runId={runId}` | Read project metadata for an exactly matching run-bound token after fresh current `ReadProjects` authorization. Purpose-bound, unbound, or mismatched tokens and duplicate/unknown query parameters are denied; the response is `Cache-Control: no-store`. |
| `POST /internal/projects/{projectId}/runs/{runId}/broker-run-binding` | Private Broker-only proof read, enabled only when `ProjectsConfig:Authentication:BrokerRunBindingAudience` is configured. It requires native TLS 1.2 or TLS 1.3, accepts no query or body, and returns a bounded `Cache-Control: no-store` receipt only for the active project, current actor membership, active Orchestrator assignment, exact project/run token binding, and matching run selection. |

Contract version 1 contains the caller and selected tenant, current membership revision, optional project/run binding, and grouped effective permissions with their current role revisions. It omits assignment IDs and raw role rows. Purpose-bound tokens are denied; resource services must request fresh context for each privileged operation and must not cache or pin it.

## Projects & Config host configuration

For complete deployment-setting guidance, see
[Runtime configuration](./runtime-configuration.md).

| Key | Requirement |
| --- | --- |
| `ProjectsConfig:Authentication:BrokerRunBindingAudience` | Optional, distinct HTTPS audience for the private Broker proof route. When configured, the route requires its TLS certificate, private key, and server-certificate digest. |
| `ProjectsConfig:Tls:CertificatePath` / `KeyPath` | Absolute paths to the existing Projects server certificate and private key. The certificate must be current, non-CA, and valid for server authentication. |
| `ProjectsConfig:Tls:ServerCertificateSha256` | SHA-256 hex digest of the exact leaf certificate DER used by Kestrel. |
| `ConnectionStrings:ProjectsConfigMigration` | Separate password-free PostgreSQL connection for `--migrate` and `--bootstrap-first-tenant-admin`. Its Entra role must own the Projects & Config schema and migration history. |
| `ProjectsConfig:Migration:AccessToken` | Optional short-lived PostgreSQL Entra access token for the explicit migration connection. The command supplies it as the PostgreSQL password. Keep it in a short-lived Secret or environment variable, never in arguments or logs. |
| `ProjectsConfig:Migration:WorkloadIdentity:{TenantId,ClientId,TokenFilePath}` | Alternative explicit migration credential used by `--migrate` and the bootstrap command. Configure either this identity or `ProjectsConfig:Migration:AccessToken`, not both. |
| `ProjectsConfig:Authentication:Authority` and `ProjectsConfig:PrivilegedSetup:{ApprovedIssuer,ApprovedSubject,ApprovedTenantId}` | The approved identity and tenant for the first TenantAdmin. The approved issuer must match the authentication authority. The command does not accept a different identity as an argument. |

The deployment profile also configures Kestrel's default certificate paths.
Those paths and the Projects TLS settings must identify the same certificate.
The service accepts the private proof request only after native TLS 1.2 or TLS
1.3; forwarded HTTPS headers are not proof.

Run `dotnet Agentweaver.Projects.Config.dll --bootstrap-first-tenant-admin`
once as an operator after the first deployment. The command uses only the
approved identity settings and the separate migration connection. It creates
the active tenant membership, TenantAdmin assignment, and authority audit rows
in one transaction. It exits without changes when that identity already has
the active grant. It refuses when another active TenantAdmin exists. Normal
host startup never runs this command.

The runtime database principal is read-only on authority tables, with one
exception. The project role-assignment API can insert project role
assignments, mark them revoked, and insert matching `authority_audit` rows.
The API, not the database grant, enforces who can make these changes: only a
caller with the current TenantAdmin role, checked in the same serializable
transaction as the change. The runtime principal can't delete authority rows
or write tenant memberships and tenant-level roles. See
[Runtime authority writes](runtime-configuration.md#runtime-authority-writes)
for the exact grants and [Project role assignments](api.md#project-role-assignments)
for the routes.

## Knowledge and Memory

These routes belong to the unpublished `Agentweaver.Knowledge` .NET 10 service
candidate. Protected routes require the configured OpenIddict issuer and audience.
Each privileged request forwards its original validated bearer token to Projects &
Config for a fresh authorization-context check; Knowledge does not keep memberships,
roles, or authorization caches. Private content reads and writes require fresh effective
`AccessPrivateKnowledge` for the target project; `ReadProjects` alone is metadata-only
and does not authorize private Knowledge records, revisions, or context. A run-bound
project Owner with `projects.admin` retains this Knowledge-specific permission but
not generic `WriteProjects`. A run-bound Owner also receives it with `api.read` and
`projects.orchestrator` when the same current project has an Orchestrator assignment
and the token is bound to the exact project/run. Memory-provider
resolution additionally requires effective project `ReadRunSelection`. If the validated
caller token is already bound to a project/run, those bindings must match the requested
route and the authority response.

| Method and path | Contract |
| --- | --- |
| `GET /health/live` | Process liveness. |
| `GET /health/ready` | PostgreSQL and current owned-schema readiness; returns `503` when migrations, tables, or required runtime grants are missing. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records` | Create a Memory, SessionContext, or Proposal record. Requires one `Idempotency-Key`; a new write returns `201`, an identical retry returns `200`, and reuse with different request content returns `409`. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records?kind={kind}&q={text}&includeInactive={bool}&page={n}&pageSize={n}` | Search only the requested project and agent, with bounded pages; requires current `AccessPrivateKnowledge` for private content. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}` | Read one record in the requested project/agent scope; requires current `AccessPrivateKnowledge` for private content. |
| `PUT /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}` | Append a revision using `expectedRevision` compare-and-swap and an `Idempotency-Key`; stale revisions return `409`. You can archive Memory and Decision records, but you can supersede only Decisions; the replacement link must identify a Decision. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}/revisions?page={n}&pageSize={n}` | Read bounded immutable revision history; requires current `AccessPrivateKnowledge` for private content. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}/restore` | Body selects a historical `revision` and supplies current `expectedRevision` plus optional `reason`; with an `Idempotency-Key`, appends a new head rather than rewinding history. The restored record is Active+Pending; a Decision requires explicit approval before it is trusted again. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/{recordId}/approve` | Explicitly approve an Active Pending or Legacy Decision using `expectedRevision`, optional `reason`, and an `Idempotency-Key`; appends an approval revision. This does not promote a proposal or deliver an accepted project fact. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/export` | No-store export of the exact project/agent's Memory and Decision records with complete immutable revision chains in `agentweaver.knowledge-transfer.v1` schema 1; bounded to 25 records, 500 revisions, and 1 MiB. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/records/import` | Import a version-1 transfer bundle whose project and agent must match the authorized route; requires an `Idempotency-Key`. Preserves transferred history and appends an `imported` revision, setting current records Active+Pending. Unsupported/incomplete input returns `400`, collisions or graph conflicts return `409`, and size-limit violations return `413`; initial and identical requests return `201` and `200`. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/proposals/{proposalId}/promote` | Requires current `AccessPrivateKnowledge`; explicitly promote an owned pending proposal using its expected revision and an `Idempotency-Key`. Events separately requires current `WriteProjects`, so the response can report `PENDING` without undoing the committed promotion. An identical retry uses the same immutable receipt. |
| `POST /api/projects/{projectId}/runs/{runId}/agents/{agentId}/proposals/{proposalId}/reject` | Explicitly reject a pending proposal using its expected revision and an `Idempotency-Key`. |
| `GET /api/projects/{projectId}/runs/{runId}/agents/{agentId}/context?q={text}&maxItems={n}&maxTokens={n}` | Compose bounded context with immutable revision references; requires current `AccessPrivateKnowledge` for private content. Invalid narrowing is `400`; mandatory-content, candidate, or output budget overflow is returned explicitly as `413`. |
| `GET /internal/accepted-effects/{receiptId}` | Legacy no-store redacted accepted-effect receipt from native PostgreSQL for the original issuer/subject and matching bounds, after a fresh current project `WriteProjects` check. Does not return proposal or decision content. |
| `GET /internal/projects/{projectId}/runs/{runId}/accepted-effects/{receiptId}` | Current no-store redacted receipt scoped to the selected, already-pinned Memory provider and exact project/run; requires current `WriteProjects`. A missing or changed immutable provider binding fails closed without creating a replacement or falling back to the legacy route. |

The service owns a separate `knowledge` PostgreSQL schema. Revisions are append-only,
provider bindings are immutable, and current records cannot be physically deleted;
record changes use expected revisions. Restore appends a new revision based on a
historical snapshot and never removes later history. Decision restore resets trust to
Pending; a separate explicit approval appends the approval revision. Versioned
transfers preserve the complete Memory/Decision revision chains and provenance while
rejecting ID collisions instead of merging record heads; imported records are
Active+Pending. Proposal promotion checks current
`AccessPrivateKnowledge`, agent ownership, source run, pending state, and expected revision. It
commits the proposal revision, approved decision, redacted immutable receipt, and
Knowledge-owned outbox intent in one transaction. Events fetches the receipt and
appends a separate project fact; this is not a native Sessions journal event. Failed
delivery remains `PENDING` until a fresh authorized caller retries.

For complete runtime-setting guidance, see
[Runtime configuration](./runtime-configuration.md).

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
| `Knowledge:CosmosProvider:{Endpoint,DatabaseId,ContainerId,ResourceId,ResourceGeneration,OptionsRevision,OptionsSchemaVersion}` | Optional Cosmos Memory adapter configuration. The catalog must register the adapter and a run must select it; negotiation requires the existing container partition key `/projectId`, the search composite index, and a non-expiring default TTL. The schema version defaults to `1`. These settings do not provision Cosmos resources. |
| `Knowledge:RedisProvider:{Endpoint,ResourceId,ResourceGeneration,OptionsRevision,OptionsSchemaVersion,Database,KeyPrefix,UserName,Password}` | Optional Redis Memory adapter configuration. The catalog must register `redis.memory` and a run must select it. `Endpoint` must be an absolute `rediss://` URI with an explicit port; ACL username/password must be paired. Negotiation requires a standalone Redis 6+ primary with healthy AOF-always persistence, noeviction, no replicas or cluster, and persistent namespace data. Defaults are schema version `1`, database `0`, and key prefix `agentweaver:knowledge`. These settings do not provision Redis resources. |
| `Knowledge:Context:{MaximumCandidates,MaximumItems,MaximumTokens}` | Service-owned hard limits; each request can only narrow them, and a configured run prompt-token limit can only narrow the context-token ceiling further. |

Both PostgreSQL connections omit passwords and use TLS `VerifyFull` with the
PostgreSQL Entra token scope. Ordinary startup verifies the already-applied schema and
requires the runtime identity to have only the necessary DML grants; it cannot migrate.
Run the executable with only `--migrate` to use the separate privileged identity. See
[Knowledge and Memory](../architecture/knowledge-memory) for ownership, context, and
current event-delivery boundaries.

## Identity host configuration

For complete deployment-setting guidance, see
[Runtime configuration](./runtime-configuration.md).

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
| `IdentityBroker:WebOrigin` | Optional normalized HTTPS origin of the Web application; required when `IdentityBroker:GitHubRepoApp` is configured. |
| `IdentityBroker:SecretRedemption:Audience` | Required HTTPS audience registered as a client resource. |
| `IdentityBroker:SecretRedemption:VaultUri` | Azure Key Vault root URI. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId` | Explicit Entra tenant ID. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityClientId` | Explicit Entra client ID. |
| `IdentityBroker:SecretRedemption:WorkloadIdentityTokenFilePath` | Absolute projected token-file path. |
| `IdentityBroker:RunBinding:ProjectsOwnerAddress` / `ProjectsAudience` | Fixed absolute HTTPS Projects & Config owner root and the distinct private audience used for short-lived Broker-to-Projects run-binding proof requests. |
| `IdentityBroker:RunBinding:ProjectsTrustBundleSha256` | SHA-256 of the exact raw UTF-8 PEM bytes at the fixed `/var/run/service-backend-ca/ca.crt` path. The bundle must contain CA certificates only and is used as the custom trust root for Projects. |
| `IdentityBroker:RunBinding:ProjectsServerCertificateSha256` | SHA-256 hex pin of the Projects leaf certificate DER bytes. Broker also checks hostname, validity, server authentication purpose, and the custom-root chain; redirects are disabled. |
| `IdentityBroker:RemoteMcpOAuth:ProjectsOwnerAddress` | Fixed absolute HTTPS root for current Projects authorization and project status. |
| `IdentityBroker:RemoteMcpOAuth:EnvironmentOwnerAddress` | Fixed absolute HTTPS root for Environment connection configuration reads and identity-binding updates. Both Remote MCP owner addresses are required when this section is configured. |
| `IdentityBroker:RemoteMcpOAuth:Providers[n]` | Each fixed provider has exact `IssuerUri`, `ClientId`, `RedirectUri`, and nonempty `ApprovedResources`, `ApprovedOAuthEndpoints`, and `ApprovedScopes`. Consent metadata reads use only the exact approved resource and issuer URLs, reject redirects, and accept discovered authorization/token endpoints only when they exactly match `ApprovedOAuthEndpoints`. |
| `IdentityBroker:GitHubRepoApp:OAuthClientId` / `OAuthClientSecret` | Registered GitHub OAuth client used only for the Repo App user connection; the secret is deployment configuration, not a database value. |
| `IdentityBroker:GitHubRepoApp:CallbackUri` | Absolute HTTPS `/auth/github/repo-app/callback` URI with no query, user info, or fragment. |
| `IdentityBroker:GitHubRepoApp:AppId` / `AppSlug` | Exact configured GitHub App identity and install URL slug. |
| `IdentityBroker:GitHubRepoApp:PrivateKeySecretId` / `PrivateKeySecretVersion` | Exact protected App key reference redeemed inside Identity for the active run; the key value is never returned or stored in Identity tables. |
| `IdentityBroker:RuntimeBootstrap:OrchestratorOwnerAddress` / `EnvironmentOwnerAddress` | Optional composition; both fixed HTTPS root owner addresses are required when configured. |
| `IdentityBroker:RuntimeBootstrap:BootstrapLifetime` / `SourceLifetime` | Explicit positive lifetimes bounded by the current registration and actor expiry. |

The host does not use ambient credentials or a development-certificate fallback.

`ProjectsConfig:Authentication:BrokerRunBindingAudience` is an optional, distinct
absolute HTTPS audience. Configuring this audience activates the private
run-binding route. Projects & Config validates its short-lived signed Broker
request tokens separately from public bearer tokens.

## Identity PostgreSQL access

Runtime and schema migration use separate connection strings, projected workload identities, and Entra PostgreSQL roles. Both connections use passwordless async Npgsql token acquisition, `VerifyFull`, and the scope `https://ossrdbms-aad.database.windows.net/.default`.

| Operation | Configuration | PostgreSQL role and boundary |
| --- | --- | --- |
| Ordinary runtime | `ConnectionStrings:IdentityBroker`; `IdentityBroker:SecretRedemption:WorkloadIdentityTenantId`, `WorkloadIdentityClientId`, and `WorkloadIdentityTokenFilePath`. | The operator-selected runtime Entra role has `CONNECT`, schema `USAGE`, DML on current Identity/OpenIddict tables, and `SELECT` on the migration history. It does not own the schema or apply migrations. |
| Explicit migration | Run the executable with only `--migrate`; provide `ConnectionStrings:IdentityBrokerMigration`, `IdentityBroker:Migration:RuntimeRole`, and `IdentityBroker:Migration:WorkloadIdentityTenantId`, `WorkloadIdentityClientId`, and `WorkloadIdentityTokenFilePath`. | A separate migration Entra role owns `identity_broker`, applies migrations, and applies the canonical explicit table grants to the named runtime role. Its workload identity receives no Azure resource role. |

The database bootstrap and reviewed grant SQL are operator-run steps. The ordinary host checks that the schema exists and no migrations are pending; it fails rather than creating roles or changing the schema.
The four runtime grant tables have narrower masks: grant heads allow
`SELECT`, `INSERT`, and `UPDATE`; revisions, operations, and receipts allow
only `SELECT` and `INSERT`. Runtime deletion, audit updates, schema creation,
and migration-history writes remain forbidden.

## AKS Application Routing preview

`appRoutingDnsZoneResourceIds` is an optional full-foundation input. Empty input requests AKS's managed default domain. One to five unique existing public or private DNS zone IDs request custom domains and disable that default. IDs must belong to the exact authorized subscription, cannot name PrivateLink zones, and can use at most one resource group per zone kind. The existing-cluster `aks-redeploy.bicep` path does not accept this input or change routing configuration.

The full-foundation `appRoutingDomain` output has `managedDefaultRequested` and `domainName` fields. For an empty zone list, the template returns the exact `defaultDomain.domainName` from the AKS response. For custom zones, it returns `domainName: null`. The AKS-only output reports the existing `defaultDomain.enabled` and `defaultDomain.domainName` values, or `false` and `null` when AKS omits `defaultDomain`. Neither path constructs hostnames or configures HTTP routes.

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

The Foundation Probe Job reports its resource-operation results, but it does not attest to its own pod identity, pulled image, or exit status. The separate read-only consumer observes the completed Job and pod. It checks
their ownership and workload-identity projection. It also verifies that the Job
image and observed pulled image match the expected registry manifest.

The consumer validates the native probe receipt against the admitted source SHA, Git tree, deployment, identity, and target. It then queries Azure Monitor only after Job completion. It requires fresh `AppDependencies` or `AppRequests` evidence correlated by source SHA, Git tree, nonce, trace, and span. Configuration checks do not substitute for this runtime proof. Incomplete or mismatched evidence remains blocked. Local fixtures validate the consumer contract but do not prove a live deployment.

## Project skill content

Projects & Config validates skill markdown and text resources before storage.
`POST /api/skills/preview` returns a content digest without storing the upload.
`POST /api/projects/{projectId}/skills/import` stores a new immutable revision
and returns its receipt. The request supplies the expected digest, an idempotency
key, and optional expected revision and source provenance. A repeated key with
different request data or a stale revision returns a conflict.

Marketplace-selected content uses separate owner routes and versioned Gateway
aliases:

- `POST /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview`
  (Gateway: `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview`)
  accepts `expectedSourceRevision`,
  `resolvedCommitSha`, and `selectedPath`. It returns validation metadata and a
  content digest, not the selected file bytes.
- `POST /api/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import`
  (Gateway: `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import`)
  accepts the same source selection plus
  `idempotencyKey` and the previewed `expectedContentDigest`. Optional `skillId`
  and `expectedRevision` target an existing skill revision. The owner returns
  the persisted revision receipt and records source provenance from its current
  source row and the pinned commit/path.

These requests contain no file bytes and accept no query parameters. Projects &
Config fetches selected files at the supplied resolved commit, checks current
actor and project authority, then rechecks the source revision. Gateway forwards
the validated bearer and optional tenant selector and preserves owner statuses
and response bodies. Gateway limits these request bodies to 3 MiB, matching the
inline preview and import routes. Owner Problem Details report invalid requests or content as `400`. They report
missing projects or sources as `404`. They report stale source/skill revisions,
digest changes, or idempotency conflicts as `409`. They report oversized source
data as `422`, content-store integrity errors as `500`, and malformed upstream
data as `502`. They report unavailable source/content storage as `503` and
upstream timeouts as `504`. Runtime-loaded status is not part of these contracts.

`GET /api/projects/{projectId}/skills?cursor={token}&pageSize={n}` lists stored skill
revision metadata and exact current assignments. It includes unassigned and
revoked revisions, source provenance, and the current project configuration
revision. Omit `cursor` for the first page. The response supplies a URL-safe
opaque `nextCursor` when another page is available. Cursors bind the project,
page size, and versioned `skillId`-ascending/revision-descending order; clients
must forward them unchanged. Pages are bounded: the default size is 25 and the
maximum is 50. Newly inserted rows before a cursor do not shift later pages;
restart without a cursor to include them. Current tenant administrators and
project owners can read active or archived project metadata while their
authority remains current; Viewers and callers with revoked or stale authority
are denied. The endpoint returns no skill instructions, resource bytes, or
storage object keys. Revoked metadata does not permit runtime use.

`PUT /api/projects/{projectId}/skills/{skillId}/assignment` appends a project
configuration revision. An enabled `SkillCatalogSetting` includes the exact
`revision`, lowercase SHA-256 `contentDigest`, and non-empty `agentIds` assigned
to current project cast agents. `POST
/api/projects/{projectId}/skills/{skillId}/revisions/{revision}/revoke` permanently
revokes that immutable revision. `GET
/api/projects/{projectId}/runs/{runId}/agents/{agentId}/skills` returns the
accepted content only after current Orchestrator authority and run binding checks.
It does not grant tool or runtime permission.

The uploaded `SKILL.md` and bundled resources are stored as one immutable object.
Configure `ProjectsConfig:SkillContent:ContainerUri` with an explicit HTTPS
container URI and grant the Projects Config workload identity Blob Data Contributor
on that container only. Missing storage configuration or missing/corrupt objects
fail closed.
