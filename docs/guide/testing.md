# Testing

The v1 tests use in-memory providers, fake Azure SDK transports, and disposable PostgreSQL. They do not require an Azure subscription.

| Test suite | What it exercises | What it does not prove |
| --- | --- | --- |
| Orchestrator Core tests | Workflow grammar and step DAG validation, bounded WorkPlan eligibility and joins, pinned model/provider selection checks, output-path validation/serialization, immutable snapshots, and scope diffs. | Child dispatch, journal persistence, approval transport, or runtime checkpoint recovery. |
| Provider tests | Catalog validation, cardinality, overrides, capability negotiation, and run pinning. | Resource provisioning or network-policy enforcement. |
| Identity library tests | Exact actor, project, run, purpose, secret ID, and version grants. They cover revocation races, expiry, cancellation, and credential invalidation. | Network authentication or a deployed broker. |
| Identity broker tests | OpenIddict validation, external login, consent, S256 PKCE, refresh replay, PostgreSQL grants, redemption HTTP, and real broker-issued tokens against Projects API source-owned memberships/roles, including forged-claim rejection and revocation. | Deployed OAuth, Azure RBAC, or Azure acceptance. |
| PostgreSQL tests | Outbox and inbox transactions, duplicates, concurrency, leases, relay outcomes, and recovery across restart. | A broker, relay daemon, exactly-once delivery, or cross-service transaction. |
| Events & Sessions tests | Provider-neutral contracts and the P0 Identity Broker principal profile; explicit runtime/migration Entra configuration with no identity fallback; PostgreSQL token scope, expired-token refresh, callback failure without stale-token fallback, and password rejection; project/run-scoped IDs; append, deduplication/conflicts, duplicate-reference rejection, contiguous ordering across sessions, replay, reconnectable cursors, provider-pin immutability, migration, and transaction rollback. PostgreSQL coverage uses disposable containers. | A deployed service, workload-identity federation, production Entra grants, live cloud migration, AgentHost integration, cross-service workflow, or production-scale replica behavior. |
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
npm run coverage:dotnet
npm run coverage:node
node --test scripts\coverage\tests\*.test.mjs
npm run test:azure
npm run test:release
```

Missing Docker, image-pull permission, or PostgreSQL startup fails the integration suite. Testcontainers does not clean up unrelated containers or contact production resources.

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
