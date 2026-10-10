# Testing

The v1 tests use in-memory providers, fake Azure SDK transports, and disposable PostgreSQL. They do not require an Azure subscription.

| Test suite | What it exercises | What it does not prove |
| --- | --- | --- |
| Orchestrator Core tests | Typed outcome/workflow/plan/revision validation, workflow grammar and step DAG validation, bounded WorkPlan eligibility and joins, project-scoped backlog graph edits and cycle checks, revision-fenced readiness for merge-complete and verified non-merge completion, MAF witness serialization and current-capture inventory regressions for late non-empty captures and multiple captures, pinned model/provider selection checks, structured scope diffs, AGT Policy provider validation and evaluation, negotiation/pinning, deny-by-default and project-policy narrowing, plus action-guard grant scope/fence checks, immutable owner receipt persistence, journal-ACK ordering, current-grant revalidation, and no-effect behavior on evidence failure. Its PostgreSQL integration tests cover persisted backlog edits, concurrent claim arbitration, idempotent replay after store restart, rollback of claim/root/decision intent, and rollback when current authority is lost immediately before commit or replay commit. | Current network authorization in backlog routes, production task-to-whole-plan evidence association/output-proof verification, live model output, full child dispatch, deployed authorization, or protected-effect call-site enforcement. |
| Source Control tests | Real temporary-Git workspace isolation and recovery, controlled GitHub HTTP behavior including exact open-PR reuse and exact-head check evidence, raw-byte HMAC and repository binding, strict Broker redemption, immutable provider-pin restore, typed approval-linked merge-grant state, native PostgreSQL repository locking and webhook-delivery replay/conflict, plus Orchestrator audience rejection of direct GitHub POST before Projects or owner effects. | A deployed Source Control service, trusted unattended webhook relay, live GitHub/Identity calls, or automatic workflow execution from a webhook. |
| Accepted-run skill reader tests | Controlled Projects HTTP responses cover original bearer/tenant forwarding, exact registration and selection hashes/revisions, ordered agent assignments, immutable content/resource hashes, legacy unpinned denial, no-store/error responses, and revocation during the content wait. Existing reviewed-snapshot reader tests retain byte-stable selections and current-authority checks. | A deployed skill owner, wired runtime route/SDK loader, live skill loading, or complete skill-import management. |
| Provider tests | Catalog validation, cardinality, overrides, capability negotiation, and run pinning. | Resource provisioning or network-policy enforcement. |
| Azure Files provider tests | Environment-scoped generation PVC identities, pinned Kubernetes API target, requested/configured StorageClass matching, effective reclaim-policy enforcement, claim/PV ownership checks, reclaim/delete UID preconditions, exact release receipts and retry outcomes, Azure Files mount options, Kubernetes quantity normalization, and credential-free request bodies through fake Kubernetes transports. | A live Kubernetes API/CSI driver, Azure Files durability or data erasure, Sandbox attachment, or durable flush. |
| Environment workspace-volume tests | Real PostgreSQL owner CAS and replay, pinned provider binding, fresh Projects authorization, retry-safe no-effect rejections and HTTP status mapping, Release rejection while bound or attached, uncertain Release retry with the same idempotency key, Replace cleanup leases/retries, stale-fence rejection, and truthful blocked/pending cleanup outcomes through fake providers and Projects clients. | A deployed Environment service, live Kubernetes/CSI behavior, Azure Files data erasure, Sandbox mounting, or durable flush. |
| Identity library tests | Exact actor, project, run, purpose, secret ID, and version grants. They cover revocation races, expiry, cancellation, and credential invalidation. | Network authentication or a deployed broker. |
| Identity broker tests | OpenIddict validation, external login, consent, S256 PKCE, refresh replay, PostgreSQL grants, redemption HTTP, and real broker-issued tokens against Projects API source-owned memberships/roles, including forged-claim rejection and revocation. GitHub Repo App PostgreSQL tests cover owner-bound OAuth and installation callbacks, PKCE/state/cookie replay rejection, refresh rotation/revocation/uncertain outcomes, installation/repository discovery, hashed selection codes, same-project reuse and cross-project denial, exact one-repository permission requests and expiry/digest checks, and absence of token/key values from Identity rows. Broker-backed Orchestrator/Events integration proves fork denial without reservation, reserved fork registration/replay, revocation before journal commit with no target/lineage change, and explicit unregistered outcomes. It also proves Projects revocation during late fork-command INSERT, owner outbox UPDATE, and registered-duplicate row-lock waits; exact current-fence runtime-owner context absence; and failure/recovery decision, gate, grant, context-history, and restart behavior. Source Control integration proves typed merge approval through real Projects, Broker, and Orchestrator hosts; persisted conflicts with no merge write for revocation during repository-lock wait, redemption, readiness, and the `merge_started` check; truthful merged SHA after post-effect revocation; and replay from a fresh Orchestrator host. **COUPLED fence proof:** the real PostgreSQL advisory-lock wait precedes a Core turn-failure transition while Projects authorization remains valid; the newer decision/fence supersedes the typed approval and the current-grant lookup plus owner CAS confirms the exact grant is no longer current before persisting `source_control_run_binding_changed` with no merge PUT. The same flow rejects a changed accepted-selection PUT and checks the current GET is byte- and hash-identical, including its revision fields, both after the PUT and after the fence change. Concurrent HTTP executions wait on the repository lock, return the same merged SHA with one GitHub merge PUT, and replay from a fresh host. Authorized workspace preparation/diff uses distinct API and checkout `SecretRef` purposes and a dual API/Broker audience token against a real temporary Git origin; responses, workspace manifest, and Git config contain no checkout credential. Other coverage includes current selection authority, typed decision/gate ownership, grant and receipt persistence, Sandbox resolution and revocation, concurrent proposal idempotency, and Policy receipt admission under expired/superseded grants, current Deny, and Core-role revocation after SQL lock waits. | Deployed OAuth, Azure RBAC, Azure acceptance, production Sandbox provisioning/dispatch, an active background relay, automatic AgentHost scheduling, or gate approval/completion from message receipt. |
| Gateway/BFF tests | Broker-signed RSA bearer validation, wrong-audience and missing-token rejection before owner calls, real Projects & Config/PostgreSQL authorization, tenant and bearer forwarding, owner `202` and stale-gate `409` passthrough, owner outage and timeout mapping, complete OpenAPI route/schema/reference checks against serialized Projects and Orchestrator DTOs, replay/reconnect cursors, pre-start SSE owner failure mapping, and stream abort after an Events body failure or current Viewer revocation. Events pages use a controlled owner HTTP transport in this boundary test. | Deployed Gateway, public DNS/TLS, production owner routing, automatic AgentHost integration, or cloud acceptance. |
| MCP tests | Native Streamable HTTP initialize/list/call, live Gateway OpenAPI tool schemas, schema-invalid enum and array-item rejection before dispatch, fixed route/argument mapping, idempotent retries, owner denial/status/outage preservation, post-headers body-stall timeout, public RFC 9728 metadata and challenge, plus real OpenIddict issuer/audience/expiry/signing-key rejection. The Broker integration test issues a real OAuth access token and verifies it is accepted by MCP and Gateway and reaches the controlled Knowledge owner unchanged with the idempotency key. | A deployed MCP host, live Knowledge/PostgreSQL owner authorization, external MCP client interoperability, or cloud acceptance. |
| Web client tests | Broker-token handling and exact project/run scoping; Gateway error, idempotency, and SSE parsing; cookie/CSRF Repo App Broker DTOs, native popup form posts, callback origin/source/request matching, deferred-CSRF cancellation across navigation/reopen, stale status rejection, and run-bound Gateway installation/pinning; revisioned project configuration; paginated Knowledge records; owner-snapshot refresh and stale-gate responses; exact request-ID/state-version gate actions; journal replay/live ordering; and truthful accepted-selection and usage-total rendering. Run with `npm --prefix apps/web test`; typecheck, lint, coverage, and production build use the scripts documented in the [web client guide](./web-client). | A live Gateway/Broker session, real owner acceptance, deployed UI/image, automatic AgentHost scheduling, model execution, or cloud acceptance. |
| Web production-container check | `node --test scripts/release/tests/web-container-callback.test.mjs` builds the pinned ASP.NET Core 10 image without network access and verifies non-root execution, callback/runtime-config security headers, callback query stripping and execution, runtime configuration encoding, hashed-asset caching, missing-asset 404, SPA fallback, and callback-log redaction. CI preloads `mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4` for `linux/amd64` before Node coverage. Locally, Linux amd64 Docker must have this exact runtime image cached. | Live Broker/Gateway browser journeys, deployed DNS/TLS/ingress, or cloud acceptance. |
| PostgreSQL tests | Outbox and inbox transactions, duplicates, concurrency, leases, relay outcomes, and recovery across restart. | A broker, relay daemon, exactly-once delivery, or cross-service transaction. |
| Events & Sessions tests | Provider-neutral contracts and the P0 Identity Broker principal profile; explicit runtime/migration Entra configuration with no identity fallback; PostgreSQL token refresh and password rejection; project/run-scoped IDs; append, deduplication/conflicts, PolicyEvaluation redaction and provenance checks, receipt-reference admission/no-store acknowledgments/transaction rollback, legacy capability pins, fork prefix lineage and authenticated idempotent retries, owner-admission revocation, migration, and rollback. Addressed-message tests cover idempotency, ordering, leases/fencing, transactional outbox, acknowledgments, expiry, and undeliverable state. PostgreSQL coverage uses disposable containers. | Workload-identity federation, production Entra grants, live cloud migration, AgentHost integration, protected-effect call-site enforcement, or production-scale replica behavior. |
| Environment egress tests | Purpose-aware FQDN/CIDR intersection, Projects authorization freshness, Cilium options and policy rendering, resource-version/generation fencing, object readback, and provider pinning with fake Kubernetes resources. | Sandbox claim/template labels, Kubernetes RBAC/workload identity, a deployed Cilium datapath, actual network reachability, or public HTTPS/Remote MCP L7 mediation. |
| Remote MCP parser and contract tests | Exact Registry response/version and static endpoint pins, tool schema and catalog digests, duplicate/invalid JSON rejection, byte limits, and unlinked delegated-OAuth configuration. | PostgreSQL migration/CAS/replay/restart behavior, a live Registry, MCP transport, reviewed tool acceptance, OAuth consent, or L7 mediation. |
| Environment Sandbox tests | See the [Sandbox testing guide](./environment-sandbox-testing.md) for owner-fenced lease, selected-provider, readiness, and recovery coverage. BuildTest source tests check complete checkpoint/profile binding, required-output failure, distinct collector terminal provenance, timeout/cancellation classification, server capability configuration, and migration/model metadata consistency without a database connection. BuildTest PostgreSQL tests create actual owner, attached Workspace, and active Sandbox records. They cover concurrent reservation, connection-pool restart replay, immutable intent, stale-binding no-effect rejection, policy-attempt persistence, timestamp precision, and terminal-evidence preservation. | Live AKS or RuntimeClass behavior, Cilium datapath enforcement, deployed AgentHost configuration, BuildTest command-Pod execution, Core run pins, or deployed service configuration. |
| Knowledge tests | Knowledge-owned PostgreSQL migrations and least-privilege runtime grants; project/agent isolation; immutable revision history, CAS conflicts, idempotent retries, explicit proposal decisions and outbox persistence; context filtering/restart; TestServer checks for fresh Projects authority and original-token forwarding; Cosmos adapter option/provider tests with a fake document store, including negotiation rejection for an expiring TTL or missing search index, batch timeout/size/throttle failures, immutable run-pin checks, and no fallback when Cosmos is unavailable; Redis adapter tests with a controlled command-client fake for TLS/options and provider selection, AOF/eviction/topology rejection, persistent-key and hash-field checks, malformed typed-payload rejection during negotiation and scans, single-write batch prevalidation, CAS, lost-response idempotency, server-time lease fencing, and backend-loss behavior; opt-in native Redis tests use the production client/store for real-Lua batch atomicity, concurrent CAS, and promotion/receipt/idempotency plus server-time lease fencing across an AOF-backed restart of one digest-pinned, test-owned instance; deterministic identical-retry races for update, reject, and promote; current scoped receipt reads, legacy three-field N-1 delivery/replay, partial-scope rejection, and a missing-pin lookup that verifies no binding is created. Identity Broker integration tests use broker-issued minimal-profile tokens, live Core membership/role lookup, the Knowledge API, and PostgreSQL to verify the private-content `WriteProjects` boundary. PostgreSQL uses disposable containers. | A deployed Knowledge service, production workload identity, live Cosmos or Redis permissions/durability/availability, or delivery from either Memory adapter to a deployed Events & Sessions journal. |
| Key Vault tests | Azure SDK authentication and secret requests through in-memory HTTP transports. Workload identity tests use generated token files and fake OAuth and Key Vault endpoints. | Live token exchange, Key Vault RBAC, or an Azure deployment. |
| Blob tests | Azure SDK requests, streamed data, create-only writes, and missing-object results through a fake HTTP transport. | Live credentials, permissions, durability, or cloud access. |
| Telemetry tests | In-process OpenTelemetry setup and Azure Monitor exporter behavior through an injected transport. | Azure Monitor ingestion. |
| Azure tooling tests | Target, source, digest, command, and acceptance guards through fake `az` and `git` executors. | A live Azure call or provisioned resource. |
| Foundation Probe tests | Receipt and token checks, provider pins, exact-version Key Vault read, owned Blob cleanup, PostgreSQL effects, and trace evidence. | Azure resource access or Identity broker redemption. |
| AgentHost tests | Actual native SDK transport, authenticated configure/refresh/A2A, exact replay, current owner/source rejection, guarded effects, turn content, idle-boundary priority, accounting acknowledgment, cache recovery, and exact startup-time ceilings. CI separately measures the compressed amd64 OCI image and starts its image-owned pinned runtime with non-executable private mounts, without network or a model call. | Automatic scheduling, deployed TLS/Sandbox placement, live OAuth/entitlement, paid model execution, or publication. |

Source Control regressions cover GitHub's actual PR-list and check-runs response shapes,
ruleset and classic branch-protection requirements, and repository issue availability.
PostgreSQL tests cover case-insensitive repository-name matching while retaining exact
provider IDs. Broker integration tests exercise the webhook route through actual Kestrel:
an authenticated, correctly signed body over 64 KiB succeeds and a body over 1 MiB is
rejected. Merge settlement coverage cancels the caller after GitHub accepts the merge and
checks that the merged SHA remains durable and can be replayed from another host.

## Run tests

Shared-model and run-admission source tests cover reference-versus-model-ID resolution,
configuration revision/hash mismatch, disabled models, accepted connection binding,
and rejection before native SDK startup.
The compiled PostgreSQL cases cover root/admission atomic persistence, replay without repricing,
hard-limit rejection, and root/backlog rollback after current-authority loss.
Events cases check that real run-start quotes create no usage or native-source rows.
Compiling these cases is not PostgreSQL execution evidence.

Numeric owner cases use the existing disposable Coordination PostgreSQL fixture and actual migrations, registrations, checkpoints, source store, and action-grant store.
They cover exact model-send reservation replay, restart without resend or refund, and concurrent child-runtime admission at the whole-run cap.
Tool cases cover permission-only checks, exact retry, changed-event conflicts, and concurrent reservations at the whole-run cap.
These store tests do not prove HTTP authentication, live native effects, or deployed Policy enforcement.
Controlled TCP tests separately exercise the actual SDK permission notification, pending-permission response, and pre-tool hook without a double debit.
Prompt tests check actual catalog/provider wire fields, missing-capacity denial, accepted narrowing, immutable source pins, and cached resume under increased or decreased capacity.
Capability-wire tests do not prove live token enforcement or truncation.

The positive Broker fixture prepares its guarded native turn through the current Orchestrator owner and persisted MAF checkpoint.
One real Events host provides pricing, accounting, and session material; the controlled SDK supplies native completion evidence, not a paid model call.

Ordinary v1 PR CI runs these PostgreSQL suites through `npm run coverage:dotnet` on its existing Linux runner.
Each suite retains standard TRX results beside Cobertura coverage and `source.json`.
The TRX logger can copy a collector report into its attachment directory.
The runner counts byte-identical copies once and still rejects missing or distinct extra reports.
Use the tested source SHA, PR head, run, and attempt to join evidence to the exact reviewed source.
A compiled case or an older green run is not a passing receipt for changed source.

Use the .NET 10 SDK selected by `global.json`, Node.js 24, and a Docker-compatible engine. PostgreSQL integration tests start their own disposable container.
CI installs Chromium for broker browser integration tests from the Release-built
`tests/Agentweaver.Identity.Broker.Tests/bin/Release/net10.0/playwright.ps1` before
`npm run coverage:dotnet`.
Browser callback fixtures also require the locked Web production shell in `apps/web/dist`;
prepare it with `npm ci --prefix apps/web --no-audit --no-fund` and
`npm --prefix apps/web run build` before running those fixtures. They configure the
normalized Broker issuer separately from the ephemeral transport URL.

Run these commands from the repository root:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GitHubRepoAppConnectionServicePostgresTests
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~SourceControlSecretRedemptionClientTests
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Mcp.Tests\Agentweaver.Mcp.Tests.csproj --no-build --no-restore --configuration Release
npm run coverage:dotnet
npm run coverage:node
node --test scripts\coverage\tests\*.test.mjs
npm run test:azure
npm run test:release
```

Missing Docker, image-pull permission, or PostgreSQL startup fails the integration suite. Testcontainers does not clean up unrelated containers or contact production resources.

### Opt-in native Redis Memory tests

`RedisMemoryNativeIntegrationTests` are skipped unless
`AGENTWEAVER_REDIS_MEMORY_NATIVE=1`. They do not create, pull, or delete a container.
The restart case restarts only the validated dedicated test instance.
Supply an already-running instance through these variables:

| Variable | Requirement |
| --- | --- |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_ENDPOINT` | A loopback `rediss://127.0.0.1:<published-port>/` endpoint. The certificate must be trusted by the test host and valid for the endpoint host; certificate validation is not bypassed. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_CONTAINER_ID` | The full 64-character ID of the dedicated container. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_OWNER_ID` | A unique 32-character run GUID. The container and AOF volume must both have labels `io.agentweaver.redis-memory.native-test=true` and `io.agentweaver.redis-memory.native-test-owner=<run-guid>`. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_IMAGE` | The exact cached image reference, including `@sha256:<64 lowercase hex digits>`. The inspected container image must match it; tests do not pull missing images. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_ENGINE` | `docker` or `podman`, used only to inspect the labeled instance and, in the restart case, restart its exact ID. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_USERNAME` and `AGENTWEAVER_REDIS_MEMORY_NATIVE_PASSWORD` | Optional existing ACL credentials. Set both or neither; the test does not create credentials or print their values. |

Publish TLS port `6380/tcp` only on the loopback host port in the endpoint. Put Redis's
active AOF directory (`dir` plus `appenddirname`, when present) on a dedicated named
volume with the same test-owner labels. Redis must pass normal provider negotiation,
including AOF-always, healthy AOF, standalone-primary, and `noeviction` checks. The
helper verifies the container ID, image, loopback binding, AOF path, and volume labels
before any write. The restart case rechecks ownership immediately before restarting
only that container, then reconnects to the same ID and pinned image. Every test uses a
fresh key prefix; there is no `FLUSHALL` or namespace-wide cleanup.

After the controlled test-owned instance is allocated, run only the native filter:

```powershell
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-restore --configuration Release --filter FullyQualifiedName~RedisMemoryNativeIntegrationTests
```

Do not set the opt-in flag until all instance, image, volume, endpoint, and ownership
values are confirmed. The native filter is not part of offline test runs.

## Reviewed remote-tool selection

Reviewed remote-tool selection tests use the actual Projects selection reader with
controlled owner responses and a controlled immutable-store resolver. They cover
exact references, duplicate and foreign references, missing or changed snapshots,
and authority loss after resolution. Project validators reject duplicate references
and retain the omitted legacy field. The numeric-limit PostgreSQL tests use the
concrete snapshot store and preserve existing grant and limit behavior.
These checks do not prove production migration activation, current remote-connection
authority, credential access, applied L7 enforcement, or a protected remote call.

## Meter-keyed Cost source selection

`AzureByokNativeUsageUsesBrokerCoreAndEventsWithExactDispatchProvenance` uses the real Broker, Projects, Core PostgreSQL, and Events owners.
Only the external native SDK transport and credential backend are controlled.
The fixture requires an accepted Azure provider pin and exact-version model-session redemption.
It records an actual dispatch-correlated SDK usage event, duplicate/conflict outcomes, and reference-only Events acknowledgment.
BYOK authority-loss cases reject stale grants, retired leases, and expiry without native or ledger effects.
The Projects fixture reuses its role-bound connection pool and closes it before role cleanup.
Provider/model mismatch and missing-measurement tests reject invalid source facts.
These checks do not call a paid model or prove Azure deployment.

Provider tests cover independent enabled selections, duplicate/disabled/wrong-seam
catalog rejection, absent sources, exact adapter/options schema requirements and
required capabilities. The Projects disposable-PostgreSQL tests use the production
catalog loader and run-selection service to persist and replay multiple source
keys, read the immutable selection, reject invalid requirements, and deny a project
Owner access without current Orchestrator authority. Existing non-Cost selection
and tenant-authorization coverage remains in the same suites. A legacy-format
regression pins the original non-Cost request serialization bytes, seeds a
pre-change snapshot with no meter-source field, and verifies replay/read without
rewriting its fingerprint or stored history.

These tests exercise candidate selection only. They do not prove SDK model-source
provenance, resource negotiation, pricing, or positive usage ingestion.

## Cost bindings, pricing, and usage storage

The additive Provider tests cover Cost resource pins, changed configuration, and
missing or changed providers. Existing candidate-selection and legacy snapshot
tests remain unchanged.

The Events & Sessions tests cover weighted nano-AIU pricing, unweighted quotes,
missing measurements, immutable rate cards, and redacted binding diagnostics.
`AzureCostProviderTests` cover model-scoped standard token arithmetic, explicit
rate-card source/currency/version, missing or unsupported token measurements,
changed pins, overflow, unsupported BYOK quotes, and provisioned-throughput
`Unpriced` behavior. These are pure local provider tests; they do not prove
trusted BYOK receipt pricing, PostgreSQL integration, or Azure price retrieval.
Hosted usage contract tests accept priced nano-AIU without optional SDK accounting metadata.
They still reject wrong source identities, meter shapes, and unpriced entries.
Cost snapshot contract tests distinguish valid zero-work quotes from unavailable pricing.
They reject source, model, rate, unit, and run-scope mismatches.
Observed snapshot tests require bounded, unique references and reject missing, changed, or foreign acknowledgments.
Native completion tests require exactly the usage-event IDs in the durable completion range.
Host tests reject a failed owner join without an answer or a second native send.
They allow explicit unpriced uncapped output and account later usage without inventing range membership.
The PostgreSQL snapshot tests cover real pricing pins without synthetic usage,
authority rollback, and Copilot-only totals with unpriced other meters.
They also join actual source and ledger rows, including explicit unpriced entries,
and reject changed registration, source, platform message, event, hash, or amount.
These tests require the disposable PostgreSQL lane; compilation is not execution evidence.
The disposable-PostgreSQL tests cover concurrent duplicates, content conflicts,
restart, immutable history, transaction rollback, and exact run/agent totals.
Receipt tests read the committed hash and immutable price through a separate
PostgreSQL connection. Duplicate retries return the identical receipt.
Migration tests cover fresh Events version 7, admitted version-3 and version-4 upgrades,
the legacy version-2 project-fact layout, and rejected version gaps.
The Orchestrator migration fixture now enumerates schema versions 8 through 15 and
includes the packaged 015 MAF evidence resource. It asserts the final migration
history contains exactly 15 versions. The fixture was not run locally during source
preparation; configured GitHub Actions CI covers it in a disposable PostgreSQL schema.
Source publication does not invoke a migration against a shared or live database.
Repeated migration checks preserve the original migration history.

The combined Broker test connects actual OAuth and bearer validation, current Core
memberships/roles, immutable Projects selection, Environment lease/profile, Orchestrator
registration and source receipt, native SDK callbacks, and Events HTTP/PostgreSQL accounting.
The test controls only external placement, SDK transport/events/catalog, and pricing inputs.
It does not seed producer authority or take a desired model from caller JSON.

The five scenarios cover successful accounting, Broker revocation before SDK creation,
observe-grant revocation during an observation lock wait, registration revocation
before the source lock opens, and Environment retirement during an observation wait.
Three additional scenarios hold the actual SDK `status.get` response before creation.
They revoke the genuine Broker grant, retire the actual Environment lease, or wait
for the real source credential to expire. Each proves zero `session.create` requests,
source records, and accounting entries.
The positive scenario commits duplicate native callbacks to one immutable receipt,
then verifies reference-only ingestion and exact `0.00123456725 AIC` accounting.
It links a real Identity connection through controlled GitHub and native Key Vault SDK transports, then restarts the Broker.
It rotates that connection after Core accepts the selection and verifies unchanged selection bytes and hash.
The model-session response contains only the current access token and typed exact-version proof.
Response, log, and SDK-wire checks exclude refresh tokens and OAuth client secrets.
The native SDK session store and ambient configuration discovery remain disabled.
The custom filesystem captures opaque native cache bytes and restores the same logical session through the actual SDK resume call.
Missing, incompatible, or corrupt caches rebuild context from journal content without a model-turn replay.
The guarded positive scenario records the actual user and assistant content, the Policy receipt, and the native cache reference.
Separate controlled Host tests require Core's exact current-operation echo before stopping turns and around native cache writes.
They reject changed operation identity, phase version, or authority, and missing replay cache.
They do not prove a persisted Core suspend operation or PostgreSQL suspend success.
It rotates the genuine source grant before the protected turn and checks refresh replay.
Native cancellation tests hold the actual abort idle event before they admit another turn.
Failed aborts and missing idle events reject later turns and cache capture.
The multiplier is not applied a second time.
Database checks reject mutation and truncation of source and accounting history.
They check each table's statement trigger and attempt dependent, multi-table, and
`CASCADE` truncation. Stored rows and pins remain identical, and receipt replay succeeds.

The fixture disposes the receiver before its caller credential and HTTP transport.
Failure output retains the exercise error and cleanup errors.
Direct console stages identify pending owner requests, SQL waits, native SDK responses,
and cleanup when a test aborts before Xunit reports a result.
Broker hosts own their registered PostgreSQL pools.
Native fixture SQL connections use a fixture-owned data source that closes during cleanup.
Repeated-host and full-suite checks verify that owned connections return to zero after disposal.
Broker coverage includes the Agent Runtime library through the existing test project and collector.

The canonical Sandbox integration also covers public write versus internal run-read
permission, three current-authority reads, and retained-lease protection against competing retirement.
Neither placement route reads the accepted selection recursively or dispatches provider effects.
The readiness module test holds the actual PostgreSQL advisory lock during fresh Cilium and Sandbox observations.
A competing retirement waits until both readiness observations finish.
The test uses fixture-owned data sources and the retained selection snapshot without recursive selection reads.
These local tests do not prove deployed hosts, live AKS placement, or paid model output.

The [AgentHost source checks](../architecture/agenthost#focused-source-checks) give the exact focused command and image-evidence boundary.

After the Release build, run the combined source scenarios with:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~CopilotConnectionRotationPreservesAcceptedSelectionThroughNativeSdkAccounting --blame-hang-timeout 3m --blame-hang-dump-type none --logger "console;verbosity=normal" -- xUnit.ShowLiveOutput=true
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~AuthorityLossDuringSdkPreparationPreventsNativeSessionCreation
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerIssuedOwnerAndSeparateRunSelectionAuthorizeWorkspaceVolumeHttpEffects
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerTokenTraversesMcpAndGatewayToKnowledgeWithoutMutation
```

The combined case also exercises the existing coordination and Source Control regressions.
Live Xunit output identifies current native requests and cleanup if the case reaches its inactivity limit.
The focused Copilot lifecycle cases cover cookie/subject/state rejection before exchange, callback replay,
concurrent refresh, transient recovery, permanent rejection, uncertain rotation, account change, and current Core revocation.
These cases use disposable PostgreSQL and controlled external transports, not live OAuth or paid model calls.

## P1 retained-surface harness candidates

The [API adapter](https://github.com/sabbour/agentweaver/blob/v1/scripts/api-harness/README.md), [UI adapter](https://github.com/sabbour/agentweaver/blob/v1/scripts/ui-harness/README.md),
and [MCP adapter](https://github.com/sabbour/agentweaver/blob/v1/scripts/mcp-harness/README.md) selectively reuse released 0.x patterns.
They use current v1 contracts, not retired monolith routes or an inherited P0 acceptance result.

```powershell
npm run test:harness
npm run test:harness:api
npm run test:harness:ui
npm run test:harness:mcp
```

The combined command runs controlled adapter checks.
API tests use actual loopback HTTP with controlled contracts.
MCP tests use the pinned SDK with an actual loopback JSON-RPC server.
UI tests use a controlled injected-page contract, not a browser.
These checks do not prove a real cross-surface journey, native model execution, or deployment.

For an approved journey, discover the live API and MCP menus first.
Use `requireJourneyCapabilities` from `scripts/harness-shared/journey-capabilities.mjs` before dispatch.
It checks only the selected gate action, message, or journal requirements.
Missing required operations block the journey; they do not produce a skipped success.
Helper and menu presence are not proof of current DOM controls or owner authority.
Journal replay requires API SSE support, but MCP deliberately does not advertise an SSE tool.

The UI adapter requires an already authorized page and exact Broker identity metadata.
It neither starts a browser nor changes its profile or stored credentials.
Gate actions require current owner snapshots and exact request, actor, fence, state version, and answer contracts.
Decision receipts must match that fence and the next state version, including owner denials.
After awaited work, the adapter rechecks the exact page scope before final action clicks and replay proof.
Actual denials remain denials.
Message acceptance and local browser echoes cannot prove delivery or gate completion.

The retained journal exposes ordered event DTOs and opaque object references.
The public Gateway has no object-content read route.
The UI adapter checks replay pages against the current DOM, without inventing transcript text.

![Retained journal and owner evidence boundaries](/diagrams/flagship/v1-sessions-journal.png)

The retained surfaces observe owner evidence; they do not create authority from browser output.
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.png'">Open full-size PNG</a> | <a :href="'/agentweaver/v1/diagrams/flagship/v1-sessions-journal.drawio'">Open editable draw.io source</a></p>

Use the shared redacted JSONL writer and the [P1 judge brief](https://github.com/sabbour/agentweaver/blob/v1/scripts/harness-judge/README.md).
Record exact source, target, scenario, surface, and owner evidence references.
Do not persist transient raw bodies or credential values.
An expected source SHA does not observe a deployment.
Client disposal does not clean up a project, run, session, or Environment.
Product cleanup requires current authority, exact test-owned identities, and verified owner receipts.

## Validate the documentation site

Run these commands after changing a documentation page, link, or diagram:

```powershell
npm --prefix docs ci
npm run docs:build
npm run docs:check-links
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
npm run test:docs-diagrams
```

The build rejects broken internal links. The link checker verifies local pages, anchors, images, and editable diagram files. The diagram checks compare JSON, draw.io XML, PNG, and hash stamps.

VitePress uses a compatible dependency range; `docs/package-lock.json` preserves reproducible installs.
Reuse an existing compatible installation instead of reinstalling it for a version check.
Diagram export accepts compatible installed draw.io Desktop versions and records the detected version in each hash stamp.
Source, XML, PNG, and rendering-recipe integrity checks remain required.

## Azure acceptance boundary

The Bicep compiler and Kustomize checks run offline. The Foundation Probe runs in tests with local fixtures. Neither operation proves a deployed AKS cluster.

The Azure acceptance command returns `blocked` when source receipts, target evidence, or dependent resources are missing. A missing Azure environment does not pass acceptance.

Deployment requires separate approval for the exact target, subscription, source, and cost. See [Azure acceptance](./azure-acceptance) and [dedicated Azure environment](../architecture/azure).

## Planned GitHub Canvas adapter acceptance

These are **unexecuted requirements**, not a passing suite or current adapter support.
Research [#1901](https://github.com/sabbour/agentweaver/issues/1901) inspected public
declarations and fake/example scenarios; it did not run interoperability, rendering,
or restart tests. Separate implementation
[#1904](https://github.com/sabbour/agentweaver/issues/1904) consumes the
[bounded declaration/action subset and evidence](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/applications-and-surfaces.md#bounded-adapter-subset).
Reuse [#1878](https://github.com/sabbour/agentweaver/issues/1878) C1 owner-state conformance
instead of adding another lifecycle owner.

| Planned case | Required evidence |
| --- | --- |
| Discovery and validation | Pin the exact SDK/CLI/profile. Expose open/action schemas through retained web and applicable MCP/core catalogs. Reject duplicate or reserved `canvas.` actions, unsupported schemas, remote references, and oversized payloads before dispatch. Use C1's admitted schema subset, not an assumed upstream dialect. |
| Ambiguous types and instance identity | Reject unqualified duplicate provider-local types. Explicit provider selection resolves one enabled kind. Re-open focuses the same bound instance; a new instance remains distinct. |
| Artifact and presentation separation | Bind a verified immutable revision/artifact separately from the surface instance. Changed/missing bytes, unsupported content, arbitrary URLs, unsafe schemes, and unavailable renderers fail explicitly without new run pins or fallback. |
| Agent/user action parity | Invoke the same declared action from an agent/MCP call and the authenticated UI. Validate input and derive actor/session/owner authority server-side. Assert the exact owner effect and journal attribution; browser output never approves a gate or creates a grant. |
| Revocation and stale binding | Revoke authority before invocation and during an owner/bridge wait. No protected effect follows authority loss. Reject stale instances, spoofed identities, cross-session/tenant references, and unknown actions. |
| Duplicate and response loss | Lose the response after owner commit. Retry with the same core operation identity and receive one durable outcome/effect. Changed input conflicts; uncertain external effects do not generate a new identity or blind retry. |
| Warm reconnect and cold resume | Preserve the mounted panel with truthful unavailable state. Restore the same instance/revision with current authorization and a fresh URL, not a saved expired one. Respect durable removal and do not create a new run or duplicate action. |
| Bridge and renderer failure | Preserve distinct sanitized structured outcomes for invalid input, handler failure, timeout, dropped connection, missing bytes, load failure, and unsupported capability. Provider `status: ready` alone cannot mark content render-ready. |
| Non-destructive close | Inject `onClose` failure. Closing the view leaves run, journal, revision, and artifact intact; reopen retains the revision. Cleanup remains pending/failed until its actual owner receipt exists. |
| Host isolation and accessibility | Accept only the expected authenticated origin/session/instance and declared action. Reject credential exposure and arbitrary message-to-tool dispatch. Exercise keyboard open/focus/form/action/close, accessible names, focus restoration, and readable failures. This tests Agentweaver's bridge, not a recovered upstream `postMessage` protocol. |
| Revision affinity | A new adapter/content revision does not change an existing instance's accepted pins. New bindings are explicit. Exercise applicable C4 upgrade/drain/retention behavior for bundled content without making every bundle slice a prerequisite. |
| Evidence and claim boundaries | Record exact fixtures and SDK/CLI/profile provenance. Controlled contract/web/MCP tests are not live interoperability. Separately approved exact-SHA AKS C5 acceptance must exercise real retained UI/core/MCP, reconnect, and owned cleanup before deployment/publication claims. |

Implementation requires admitted research, C1, and the retained web/core action
contracts from [#1859](https://github.com/sabbour/agentweaver/issues/1859) and
[#1857](https://github.com/sabbour/agentweaver/issues/1857), with applicable
[#1858](https://github.com/sabbour/agentweaver/issues/1858) MCP operations.
It does not require all P1, the whole bundle epic, A2UI, Cosmos, Redis, or P3.
Passing documentation checks supplies no permission for live deployment, credentials,
paid execution, publication, or destructive cleanup.

## Environment remote MCP connection records

Disposable PostgreSQL tests cover connection migrations, exact Identity-binding
revisions and digests, replay after restart, changed authority, and immutable history.
Runtime-role tests reject updates to configuration history and physical deletion.
Pure parser tests cover exact Registry versions, static HTTPS endpoints, duplicate
JSON fields, tool names, schema shapes, and byte limits.
These tests do not prove live discovery, OAuth credential use, applied L7 policy,
or a protected remote request.

Project skill content has pure tests for manifest/resource validation, normalized
path collisions, digest stability, immutable object writes, and missing or corrupt
objects. The Projects Config PostgreSQL test verifies idempotent imports, revision
conflicts, assignment to active agents, and reads of the exact accepted revision
and resources. It requires the repository's Testcontainers PostgreSQL fixture.
