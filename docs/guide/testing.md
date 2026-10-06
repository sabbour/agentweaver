# Testing

The v1 tests use in-memory providers, fake Azure SDK transports, and disposable PostgreSQL. They do not require an Azure subscription.

| Test suite | What it exercises | What it does not prove |
| --- | --- | --- |
| Orchestrator Core tests | Workflow grammar and step DAG validation, bounded WorkPlan eligibility and joins, pinned model/provider selection checks, output-path validation/serialization, immutable snapshots, scope diffs, AGT Policy provider validation, negotiation/pinning, deny-by-default and project-policy narrowing, action-guard grant scope/fence denial and no-effect behavior when evidence persistence is unavailable, plus PostgreSQL owner-session/message state including blocked wakes and boundary replay. | Production grant-owner issuance/current storage, trusted Core-writer provenance, successful protected-effect authorization, child dispatch, journal persistence, approval transport, runtime checkpoint recovery, or protected-effect call-site enforcement. |
| Provider tests | Catalog validation, cardinality, overrides, capability negotiation, and run pinning. | Resource provisioning or network-policy enforcement. |
| Azure Files provider tests | Generation-stable PVC provisioning, requested/configured StorageClass matching, effective reclaim-policy enforcement, claim/PV ownership checks, reclaim/delete UID preconditions, exact release receipts and retry outcomes, Azure Files mount options, Kubernetes quantity normalization, and credential-free request bodies through fake Kubernetes transports. | A live Kubernetes API/CSI driver, Azure Files durability or data erasure, Sandbox attachment, or durable flush. |
| Environment workspace-volume tests | Real PostgreSQL owner CAS and replay, pinned provider binding, fresh Projects authorization, uncertain Release retry with the same idempotency key, Replace cleanup leases/retries, stale-fence rejection, and truthful blocked/pending cleanup outcomes through fake providers and Projects clients. | A deployed Environment service, live Kubernetes/CSI behavior, Azure Files data erasure, Sandbox mounting, or durable flush. |
| Identity library tests | Exact actor, project, run, purpose, secret ID, and version grants. They cover revocation races, expiry, cancellation, and credential invalidation. | Network authentication or a deployed broker. |
| Identity broker tests | OpenIddict validation, external login, consent, S256 PKCE, refresh replay, PostgreSQL grants, redemption HTTP, and real broker-issued tokens against Projects API source-owned memberships/roles, including forged-claim rejection and revocation. A Broker-backed HTTP integration also runs Projects, Orchestrator, and Events hosts with disposable PostgreSQL to verify accepted run selection, root/child session registration, owner-outbox message admission, delivery, journal ID mapping, turn-boundary replay, out-of-boundary claim/presentation rejection, fenced acknowledgment, correlated reply input, parent wake/notification, and live permission revocation. | Deployed OAuth, Azure RBAC, Azure acceptance, an active background relay, automatic AgentHost scheduling, or gate approval/completion from message receipt. |
| PostgreSQL tests | Outbox and inbox transactions, duplicates, concurrency, leases, relay outcomes, and recovery across restart. | A broker, relay daemon, exactly-once delivery, or cross-service transaction. |
| Events & Sessions tests | Provider-neutral contracts and the P0 Identity Broker principal profile; explicit runtime/migration Entra configuration with no identity fallback; PostgreSQL token scope, expired-token refresh, callback failure without stale-token fallback, and password rejection; project/run-scoped IDs; append, deduplication/conflicts, duplicate-reference rejection, redacted PolicyEvaluation serialization, rejection of generic PolicyEvaluation appends without trusted Core-writer provenance, event-version compatibility, contiguous ordering across sessions, replay, reconnectable cursors, provider-pin immutability, migration, and transaction rollback. Addressed-message coverage includes sender-run/session idempotency, progress history without recipient presentation, thread sequence/reply correlation, delivery-mode priority, claim lease recovery and fencing, journal/outbox transactionality, presentation and acknowledgment deduplication, expiry, stale-recipient fencing, and undeliverable state. PostgreSQL coverage uses disposable containers. | A deployed service, trusted Orchestrator Core PolicyEvaluation writer, workload-identity federation, production Entra grants, live cloud migration, AgentHost integration, production-scale replica behavior, or cross-service owner authorization beyond the documented Broker-backed integration. |
| Environment egress tests | Purpose-aware FQDN/CIDR intersection, Projects authorization freshness, Cilium options and policy rendering, resource-version/generation fencing, object readback, and provider pinning with fake Kubernetes resources. | Sandbox claim/template labels, Kubernetes RBAC/workload identity, a deployed Cilium datapath, actual network reachability, or public HTTPS/Remote MCP L7 mediation. |
| Knowledge tests | Knowledge-owned PostgreSQL migrations and least-privilege runtime grants; project/agent isolation; immutable revision history, CAS conflicts, idempotent retries, explicit proposal decisions and outbox persistence; context filtering/restart; and TestServer checks for fresh Projects authority and original-token forwarding. Identity Broker integration tests use broker-issued minimal-profile tokens, live Core membership/role lookup, the Knowledge API, and PostgreSQL to verify the private-content `WriteProjects` boundary. PostgreSQL uses disposable containers. | A deployed Knowledge service, production workload identity, or delivery from the Knowledge outbox to the native Events & Sessions journal. |
| Key Vault tests | Azure SDK authentication and secret requests through in-memory HTTP transports. Workload identity tests use generated token files and fake OAuth and Key Vault endpoints. | Live token exchange, Key Vault RBAC, or an Azure deployment. |
| Blob tests | Azure SDK requests, streamed data, create-only writes, and missing-object results through a fake HTTP transport. | Live credentials, permissions, durability, or cloud access. |
| Telemetry tests | In-process OpenTelemetry setup and Azure Monitor exporter behavior through an injected transport. | Azure Monitor ingestion. |
| Azure tooling tests | Target, source, digest, command, and acceptance guards through fake `az` and `git` executors. | A live Azure call or provisioned resource. |
| Foundation Probe tests | Receipt and token checks, provider pins, exact-version Key Vault read, owned Blob cleanup, PostgreSQL effects, and trace evidence. | Azure resource access or Identity broker redemption. |

## Run tests

Use the .NET 10 SDK selected by `global.json`, Node.js 24, and a Docker-compatible engine. PostgreSQL integration tests start their own disposable container.

Run these commands from the repository root:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-build --no-restore --configuration Release
npm run coverage:dotnet
npm run coverage:node
node --test scripts\coverage\tests\*.test.mjs
npm run test:azure
npm run test:release
```

Missing Docker, image-pull permission, or PostgreSQL startup fails the integration suite. Testcontainers does not clean up unrelated containers or contact production resources.

## Meter-keyed Cost source selection

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

## Azure acceptance boundary

The Bicep compiler and Kustomize checks run offline. The Foundation Probe runs in tests with local fixtures. Neither operation proves a deployed AKS cluster.

The Azure acceptance command returns `blocked` when source receipts, target evidence, or dependent resources are missing. A missing Azure environment does not pass acceptance.

Deployment requires separate approval for the exact target, subscription, source, and cost. See [Azure acceptance](./azure-acceptance) and [dedicated Azure environment](../architecture/azure).
