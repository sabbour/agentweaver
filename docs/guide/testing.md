# Test Agentweaver

Use the test command that matches the code or documentation you changed. A
passing test confirms only the behavior its test exercises against the source
revision that ran. Local tests do not prove that Azure resources, services, or
provider integrations are deployed.

## Prerequisites

Install the .NET 10 SDK selected by `global.json` and Node.js 24. PostgreSQL
integration tests use disposable Testcontainers databases, so install a
Docker-compatible engine and allow it to pull the test image. Do not point the
tests at a shared or production database.

Run commands from the repository root unless a procedure says otherwise.

## Choose and run a test procedure

1. Identify the service, provider, or documentation surface you changed.
2. Run the matching standard or focused command in the sections below.
3. Record the command, result, and exact source SHA. Mark unrun tests as not
   run; do not count a compile-only or older-source result as a pass.

## Run the standard test suites

Run the following commands to restore, build, and test the main .NET and Node.js
code:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
npm run coverage:dotnet
npm run coverage:node
npm run test:azure
npm run test:release
```

The commands restore the locked dependencies, build the solution, run the .NET
and Node.js suites, and check Azure and release tooling. The Azure tooling
tests use local fixtures; they do not connect to Azure. PostgreSQL-backed
suites require the container engine. A missing engine, permission to pull the
image, or database startup fails those suites; it does not count as a pass.

To run named project suites after the Release build, use:

```powershell
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GitHubRepoAppConnectionServicePostgresTests
dotnet test tests\Agentweaver.Orchestrator.Core.Tests\Agentweaver.Orchestrator.Core.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~SourceControlSecretRedemptionClientTests
dotnet test tests\Agentweaver.Identity.Tests\Agentweaver.Identity.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Environment.Tests\Agentweaver.Environment.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.Mcp.Tests\Agentweaver.Mcp.Tests.csproj --no-build --no-restore --configuration Release
```

These project suites use test-owned fixtures. Some include disposable
PostgreSQL; none of these commands publishes packages or deploys a service.
The coverage runner used in CI saves TRX results, Cobertura coverage, and
source identity alongside the test output. Match a result to the exact source
SHA, pull request head, workflow run, and attempt. A compiled test or a green
run from an older revision is not evidence that the current revision passed.

## Understand v1 pull request CI

The `v1 foundation CI` workflow keeps the required `foundation` check as an
always-reported aggregate. It fails if any selected job fails, is cancelled,
or is unexpectedly skipped. Release composition validation, controlled P1
journeys, CI/coverage guards, and issue-to-merge contracts still run on every
candidate. `v1-docs-ci.yml` remains the separate documentation check.

Changed-path detection skips expensive .NET, Web, image, and tooling suites
for documentation/metadata-only PRs. Unrelated workflow edits do not run those
suites. Unknown paths and edits to `v1-ci.yml` or CI helpers select everything.
.NET and Node tooling inputs select Web validation too: Broker browser and
Linux release-container tests consume the exact candidate's built Web
artifact. The tooling job restores its AgentHost/Web Host publish dependencies.
Broker shards also install locked Web dependencies for Vite-backed browser
fixtures; the built artifact alone does not replace their development server.
Service/package/test, lockfile and deployment
inputs select image checks, including source receipts and isolated native
prompt-capacity verification; no images are published by this workflow.
Web, AgentHost, deployment and .NET build/lock inputs also select Node tooling
because its executable Linux contracts consume those inputs.

Each .NET project runs on its own runner. The Broker suite has four stable
SHA-256 method shards, derived from the built assembly's VSTest discovery.
All theory cases for a method stay together, including methods in large
partial classes. Every Broker shard saves the complete partition and checks
its TRX method set against its assigned partition. Empty discovery, empty
shards, missing receipts and failed tests fail CI. Existing explicitly
opt-in native cases retain their skip behavior; isolated image-native checks
remain separate and mandatory when image validation is selected.

The PostgreSQL collection already shares one container per test process;
each test retains a fresh database. Browser-backed Projects tests also share
a lazily created Playwright driver, but not browsers, resolver arguments,
contexts or cookies. NuGet caches are keyed by SDK/project/lock inputs.
Chromium caches are keyed by OS, architecture and Broker package lock; the
installer still runs on cache hits to install required Linux libraries.

PR test invocations do not collect coverage. Pushes to `v1` and manual
dispatches run full coverage, instrumenting the same single test pass per
suite. The coverage job merges downloaded .NET shard reports without rerunning
tests, checks the expected production assemblies, and uploads the existing
source-bound `v1-coverage-*` artifact with Web and Node reports. Local
`npm run coverage:dotnet` retains its full sequential diagnostic behavior.
Only superseded runs for the same PR are cancelled; pushes and manual runs
have independent concurrency groups.
Manual release validation derives its comparison base from the selected
commit's first parent, preserving the validator's checked history contract
without inventing a push event or changing the tested checkout.

Run the workflow regression guards locally:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
node --test scripts\ci\tests\*.test.mjs scripts\coverage\tests\*.test.mjs
node scripts\ci\v1-test-plan.mjs matrix
```

After restoring/building a test project, use
`node scripts\ci\v1-test-plan.mjs run identity-broker-1` to reproduce a
Broker shard (Docker, Chromium and built Web assets are required). Set
`COLLECT_COVERAGE=true` to instrument that invocation. Each shard's summary
and artifact include test/discovery elapsed time; Actions job durations also
include setup, restore and build.

The historical comparison baseline was approximately 29 minutes median for
v1 PR CI versus 15 minutes for dev, with Broker alone taking 13m46s.
Performance acceptance requires at least three representative successful PR
runs, a median total duration of at most 20 minutes and a longest Broker shard
of at most 8 minutes. Record run URLs, cache warmth and runner/queue caveats
on the tracking issue before closing it; configuration changes alone do not
prove those timing targets.

## Run focused database checks

Run the Broker migration/grant and read-only bootstrap regressions against
disposable PostgreSQL:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --configuration Release --filter "FullyQualifiedName~IdentityBrokerMigrationCommandTests|FullyQualifiedName~IdentityBrokerPostgresBootstrapCommandTests"
```

These cases reproduce missing history permission, exercise the migration
owner's canonical grant step, and verify that the runtime cannot write migration
history. They do not connect to Azure or authorize a live grant.

The Foundation Probe PostgreSQL initializer has separate source and database
checks. Run the source preflight without starting PostgreSQL:

```powershell
dotnet test tests\Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresInitializerTests
```

Run the production catalog SQL cases and continuation checks against the
disposable PostgreSQL fixture:

```powershell
dotnet test tests\Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresInitializerPreflightPostgresTests
dotnet test tests\Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresContinuationPostgresTests
```

These tests check the initializer's source-defined SQL behavior, including
grant preservation, membership cleanup, and rejection of unsafe reserved
flags. They do not execute the initializer against Azure or the approved
database. See the
[Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md)
for the separate live-state and approval requirements.

## Test Gateway and authenticated client paths

The following Broker integration tests use a Broker-issued token and test-owned
Projects/PostgreSQL state. They use controlled owner transports; they do not
test deployed owner services:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~CopilotConnectionRotationPreservesAcceptedSelectionThroughNativeSdkAccounting --blame-hang-timeout 3m --blame-hang-dump-type none --logger "console;verbosity=normal" -- xUnit.ShowLiveOutput=true
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~AuthorityLossDuringSdkPreparationPreventsNativeSessionCreation
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerIssuedOwnerAndSeparateRunSelectionAuthorizeWorkspaceVolumeHttpEffects
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~BrokerTokenTraversesMcpAndGatewayToKnowledgeWithoutMutation
```

These scenarios cover authenticated Gateway forwarding, event replay and
reauthorization, account-connection lifecycle, run-bound authority, and
Knowledge access. Their real database and authorization fixtures do not prove
deployment, live OAuth, live Azure credentials, or paid model execution.

To run the six-scenario public BuildTest dispatch regression after the Release
build, use:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName=Agentweaver.Identity.Broker.Tests.ProjectsConfigBrokerAuthorizationTests.PublicBuildTestDispatchRetainsItsIntentAndRejectsChangedAuthority --collect "XPlat Code Coverage" --logger "trx;LogFileName=public-buildtest-dispatch.trx" --results-directory artifacts\tests\public-buildtest-dispatch
```

A successful run reports six executed tests with no failures, skips, or
warnings. The fixture uses broker-issued tokens, current Projects authority,
disposable PostgreSQL, a temporary Git workspace, and controlled
Environment responses. The positive case captures output through an explicit
test action from an empty Git baseline. It does not prove automatic production
capture, native Kubernetes execution, paid model execution, or deployment.

## Test native Redis Memory integration

The native Redis tests are opt-in. They do not create, pull, or delete a
container. Set `AGENTWEAVER_REDIS_MEMORY_NATIVE=1` only after you have
confirmed that the existing Redis instance and its volume are dedicated to
this test run.

Provide an already-running instance with these variables:

| Variable | Requirement |
| --- | --- |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_ENDPOINT` | Loopback `rediss://127.0.0.1:<published-port>/` endpoint. The test host must trust the certificate, and its name must match the endpoint. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_CONTAINER_ID` | Full 64-character container ID. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_OWNER_ID` | Unique 32-character run GUID. Set `io.agentweaver.redis-memory.native-test=true` and `io.agentweaver.redis-memory.native-test-owner=<run-guid>` on both the container and AOF volume. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_IMAGE` | Exact cached image reference, including `@sha256:` and 64 lowercase hexadecimal digits. The test does not pull an image. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_ENGINE` | `docker` or `podman`, used only to inspect the labeled container and restart that exact container in the restart case. |
| `AGENTWEAVER_REDIS_MEMORY_NATIVE_USERNAME` and `AGENTWEAVER_REDIS_MEMORY_NATIVE_PASSWORD` | Optional existing ACL credentials. Set both or neither. The test does not create credentials or print their values. |

Publish Redis port `6380/tcp` only on the loopback host port. Store the active
AOF directory on a dedicated named volume with the same test-owner labels.
Redis must pass provider negotiation, including AOF-always, healthy AOF,
standalone-primary, and `noeviction` checks. The helper verifies the container,
image, port binding, AOF path, and volume labels before it writes. Each test
uses a fresh key prefix; the tests do not run `FLUSHALL` or perform
namespace-wide cleanup.

After you verify every instance, image, volume, endpoint, and ownership value,
run only the native test filter:

```powershell
$env:AGENTWEAVER_REDIS_MEMORY_NATIVE = "1"
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj --no-restore --configuration Release --filter FullyQualifiedName~RedisMemoryNativeIntegrationTests
```

Do not use the opt-in flag for a shared or production Redis instance. These
tests exercise the selected Redis deployment but do not prove a deployed
Agentweaver application.

## Validate the selected Projects runtime profile

Run the profile's local source checks:

```powershell
node --test scripts\azure\tests\projects-runtime-profile.test.mjs
```

The tests check required runtime identity and database inputs, rejection of
missing configuration, immutable image references, non-root file access,
private HTTPS, bounded resources, and caller and network rules. They also
check the existing provider-catalog reference and environment prefix. They
do not read or verify a live catalog, create database roles, apply migrations
or grants, publish images, or deploy a service.

## Planned GitHub Canvas adapter acceptance

GitHub Canvas adapter cases are unexecuted requirements, not a passing suite
or current adapter support. The research recorded in
[issue #1901](https://github.com/sabbour/agentweaver/issues/1901) did not run
interoperability, rendering, or restart tests. Implementation work is tracked
in [issue #1904](https://github.com/sabbour/agentweaver/issues/1904). Do not
report the planned cases as product behavior or passing test evidence.

## Test retained API, UI, and MCP adapters

Run the controlled local adapter tests with:

```powershell
npm run test:harness
npm run test:harness:api
npm run test:harness:ui
npm run test:harness:mcp
```

The API tests use loopback HTTP and controlled contracts. The MCP tests use a
pinned SDK and loopback JSON-RPC server. The UI tests use injected page
doubles, not a browser. These tests check fixture creation, selected surface
calls, cleanup outcomes, and refusal of unverified ownership claims. They do
not prove live owner behavior, native model execution, or deployment.

The runtime-observation probe also has a focused local test:

```powershell
node --test scripts\api-harness\test\runtime-observation.test.mjs scripts\azure\p1-dispatch-recovery-probes\tests\run.test.mjs
```

This read-only probe requires an already authorized client and real Environment
creation receipt. Stored rows alone do not prove dispatch, a late provider
callback, or verified resource release. The full harness runner checks source
and deployment evidence before it reads credentials or starts a surface
client. It does not sign in, refresh tokens, or grant authority. For setup
requirements, see the
[API harness guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/api-harness/README.md),
[UI harness guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/ui-harness/README.md),
and [MCP harness guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/mcp-harness/README.md).

## Validate documentation changes

After you edit documentation, run these commands from the repository root:

```powershell
npm run docs:build
npm run docs:check-links
npm run docs:check-drift -- --base origin/v1
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
npm run test:docs-diagrams
```

The build and link checks validate published pages, anchors, and local assets.
The diagram checks validate the repository's diagram sources, rendered images,
and integrity stamps. This article rewrite does not add or change a diagram.

## Understand what each test proves

Use the following table when you report test results:

| Test area | The tests exercise | The tests do not prove |
| --- | --- | --- |
| Release and deployment tooling | Source manifests, receipts, release validation, controlled GitHub/registry/Kubernetes transports, and failure handling. | Artifact publication, write access to external registries, a live deployment, or release approval. |
| Providers and resource selection | Catalog validation, cardinality and override rules, capability negotiation, immutable selection pins, meter-source compatibility, and adapter request contracts. | Resource provisioning, SDK model-source provenance, cloud access, pricing, or network-policy enforcement. |
| Orchestrator | Workflow grammar and step-DAG validation, bounded WorkPlans, schedule and event-predicate evaluation, backlog graph edits, readiness evidence, MAF capture checks, provider pins, Policy evaluation, and action-guard receipts. PostgreSQL cases exercise backlog edits, concurrent claims, restart replay, and rollback on lost authority. | Automatic workflow initiation, full child dispatch, live model output, deployed authorization, production Prompt Shields transport, trusted Guardrails adapters, MAF gate wiring, or protected-effect call-site enforcement. A compiled PostgreSQL case is not database execution evidence. |
| Gateway and Identity | Broker-issued tokens, owner authorization fixtures, route contracts, status forwarding, current-grant checks, and reauthorization before event writes. | Deployed Gateway or owner availability, live OAuth, native TLS handshakes, live Kubernetes ownership, or an external identity-provider response. |
| AgentHost and model execution | The pinned SDK sends real test turns to an isolated loopback endpoint. Tests check bearer, model, request path, session marker, response receipt, and measured compaction under a bounded token limit. | Hosted Copilot, production BYOK access, paid model execution, or deployed runtime behavior. |
| Cost and usage | Published Copilot rate cards, model-scoped Azure token arithmetic, explicit `Unpriced` results, immutable source bindings, controlled SDK evidence, real disposable PostgreSQL transactions, and replay. The combined Broker scenario controls the external SDK transport and credential backend. | A paid model call, live Azure pricing, or deployed Broker/provider behavior. |
| Source Control and Skills | Real temporary Git workspaces, controlled GitHub HTTP, raw-byte HMAC and repository binding, immutable provider pins, webhook replay, and owner-backed Skill metadata. Skill-reader tests verify original bearer/tenant forwarding, pinned hashes, revocation, and SDK file reads. | A deployed Source Control or Skills service, live GitHub access, trusted unattended webhook delivery, runtime route deployment, or proof that a production runtime loaded a Skill. |
| Remote MCP and identity | Connection-record migrations, exact Identity-binding revisions and digests, restart replay, immutable history, owner TLS profiles, policy inputs, and denial before secret or certificate access. | A positive native mutual-TLS exchange, mapped credential-use route, applied network enforcement, credential delivery, or protected remote call. |
| Environment and Sandbox | Owner stores, bounded receipts, fake providers and Kubernetes responses, and controlled lifecycle races. | Live Kubernetes or CSI behavior, durable mounted data, Cilium enforcement, or a complete deployed suspend/resume flow. See [Environment Sandbox tests](./environment-sandbox-testing). |
| Azure tooling | Bicep and manifest source checks, local Kustomize rendering, and controlled deployment-tool transports. | An Azure subscription, live resources, cloud grants, or successful service deployment. See [Azure acceptance](./azure-acceptance). |
| Documentation | VitePress build, links, source drift, and diagram consistency. | Runtime behavior or the correctness of claims that are not covered by source checks. |

Some GitHub Canvas checks documented in the issue tracker are planned
requirements, not an implemented adapter or a passing test suite. Treat
controlled fixtures, compiled cases, and unexecuted requirements as distinct
from live or deployed evidence.

## Expected result

The selected commands exit successfully, and the recorded results identify the
source revision that ran. A passing test proves only the behavior described
for its test area; it does not prove deployment or live external access.
