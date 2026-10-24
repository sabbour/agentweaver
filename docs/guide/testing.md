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

## Test web browser journeys

The Web end-to-end tests use Playwright with fake Broker and Gateway services.
They check the built app with fixture data; they do not contact deployed
services. The Chief of Staff journey checks that Priority creation and order
survive a reload, notes save successfully, and a conversation link created from
Priorities is still shown after a reload.

Run the Web build and the focused browser journeys from the repository root:

```powershell
npm --prefix apps/web run build
node --test apps/web/e2e/cos.e2e.test.mjs apps/web/e2e/priority-links.e2e.test.mjs apps/web/e2e/artifact.e2e.test.mjs
```

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
dotnet test tests\Agentweaver.Providers.Sessions.AgentSessions.Tests\Agentweaver.Providers.Sessions.AgentSessions.Tests.csproj --no-build --no-restore --configuration Release
dotnet test tests\Agentweaver.EventsAndSessions.Tests\Agentweaver.EventsAndSessions.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~SessionsProviderAbsenceTests
```

These project suites use test-owned fixtures. Some include disposable
PostgreSQL; none of these commands publishes packages or deploys a service.
The AgentSessions suite checks export verification and replay behavior. The
absence test confirms that the native provider stays the default and the
platform works without the optional AgentSessions package.

## Test project-wide Knowledge discovery

Run the Knowledge service suite to check authorization, existing provider
bindings, and all-agent search across the native PostgreSQL, Cosmos, and Redis
providers, including global paging across distinct provider resources. Run the
Web suite to check the project-wide search, access-run links, record provenance,
and revision history alongside the run-scoped record workflows:

```powershell
dotnet test tests\Agentweaver.Knowledge.Tests\Agentweaver.Knowledge.Tests.csproj
npm --prefix apps/web test
```

These tests use test-owned stores and owner responses. They do not prove that
Knowledge or the Gateway is deployed.

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

Each .NET project runs on its own runner. The Broker suite has four
deterministic method shards, derived from the built assembly's VSTest
discovery. All theory cases for a method stay together, including methods in
large partial classes. Shards are balanced by measured duration: CI places the
slowest method first on the least-loaded shard (longest-processing-time-first).
Method weights come from `scripts/ci/broker-test-weights.json`, which records
the source run. A method without a recorded weight uses `defaultSeconds`, so
new tests are still assigned exactly once. Weights change placement only;
they never filter tests. Every Broker shard saves the complete partition and checks
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
The web job and Broker shards also cache the installed `apps/web/node_modules`
directory. The key combines OS, architecture, the exact Node.js version, and
the hash of `apps/web/package-lock.json`. The cache has no partial restore key,
so a lockfile or Node.js change misses and runs a clean `npm ci`. On an exact
hit, CI skips `npm ci`. Tool caches such as `node_modules/.vite` are excluded.
CI does not cache .NET `bin`/`obj` output or Docker layers: a reused build
output could hide a compile failure, and image builds must stay reproducible
for the image provenance checks.

Pull request, push, and manual `v1 foundation CI` runs never collect
coverage. Coverage is a separate weekly measurement; see
[Measure v1 coverage weekly](#measure-v1-coverage-weekly). Local
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

When one Broker shard becomes consistently slower than the others, refresh
the weights from the TRX receipts of a recent successful run without coverage:

```powershell
gh run download <run-id> --pattern 'v1-dotnet-identity-broker-*' --dir artifacts\broker-trx
node scripts\ci\v1-test-plan.mjs weights "v1 foundation CI run <run-id>" (Get-ChildItem artifacts\broker-trx -Recurse -Filter *.trx).FullName
```

The command sums theory-case durations per method, keeps methods that take at
least one second, and sets `defaultSeconds` to the mean of the rest. Commit the
updated file with the run URL in the pull request.

Performance acceptance for `v1 foundation CI` requires at least three
representative successful pull-request runs. The median total duration must
not exceed 20 minutes, and the longest Broker shard must not exceed 8 minutes.
Use completed run results to verify these targets. A configuration change
alone does not prove performance acceptance.

## Measure v1 coverage weekly

The `v1 coverage` workflow (`.github/workflows/v1-coverage.yml`) measures
coverage once a week. It calls `v1-ci.yml` with `collect_coverage: true`, so
the same test jobs run with instrumentation, and the same `foundation` gate
must pass. The coverage job merges the .NET shard reports without rerunning
tests and uploads the source-bound `v1-coverage-<sha>-<attempt>` artifact,
which is kept for 30 days.

Every suite artifact is bound to the run attempt that produced it. Each .NET
shard and the Node and Web coverage jobs write `attempt.json` (suite, run
attempt, and job status) and upload `<artifact>-<attempt>`, for example
`v1-dotnet-identity-broker-2-2`. **Re-run failed jobs** keeps the artifacts of
jobs that passed, so a rerun has one artifact per attempt for each rerun suite.
Before combining, `scripts/coverage/attempts.mjs select` keeps only the newest
attempt of each suite. It fails if that attempt has no marker, a marker from
another suite or attempt, or a job status other than `success`. The selected
attempt of each suite is recorded in `attempts.json` and in the `suiteAttempts`
field of `source.json`. Data from a failed attempt therefore never enters the
combined report.

GitHub runs scheduled workflows only from the default branch, which is `dev`.
A `dev` copy of `v1-coverage.yml` runs every Monday at 05:00 UTC and
dispatches the real workflow on `refs/heads/v1` with `publish=true`. To
validate coverage collection by hand, run the workflow on a branch:

```powershell
gh workflow run v1-coverage.yml --ref <branch>
gh workflow run v1-coverage.yml --ref <branch> -f publish=true
```

The first command only measures. The second also publishes. Publishing from
a branch other than `v1` replaces the current badges until the next weekly
run, so publish only to verify the publication path.

Each surface reports its own line coverage. The value is covered executable
lines divided by coverable lines in that surface's instrumented scope:

| Surface | Scope | Source report |
| --- | --- | --- |
| .NET | Production assemblies in the combined suite reports | ReportGenerator `Summary.json` |
| Web | `apps/web/src/v1` source files | Vitest `coverage-summary.json` |
| Node release tooling | `scripts/release/*.mjs` | Node test LCOV, merged per line |

The surfaces are not combined, because one overall value would be dominated
by .NET and hide the smaller surfaces. Values are rounded down to one decimal
place.

When a run publishes, `scripts/ci/v1-coverage-report.mjs`:

1. Writes one shields.io endpoint document per surface, plus `coverage.json`
   and `coverage.md`, to `v1/` on the `badges` branch. The README badges read
   these documents and link to `coverage.md`, which records the source SHA,
   run, and artifact. Only a run that passes the `foundation` gate publishes,
   and the artifact's tested SHA must match the run's SHA. The workflow never
   pushes to `v1`.
1. Creates or updates one open issue labeled `area:testing` that starts with
   the `agentweaver:v1-coverage-improvement` marker. The issue lists the
   measured values, the five largest gaps for each surface (classes or files
   with at least 10 uncovered lines), and acceptance criteria. If an open
   marker issue exists, the run updates it instead of creating another.
1. Assigns the issue to the Copilot coding agent with a `v1` base branch, then
   reads the issue back to confirm the assignment. If Copilot is already
   assigned, the run leaves the assignment unchanged.

`GITHUB_TOKEN` can't assign Copilot. As a one-time setup, an administrator
adds a repository secret named `COPILOT_ASSIGN_TOKEN` that holds a user
token: a fine-grained personal access token with read access to metadata and
read and write access to actions, contents, issues, and pull requests, or a
classic token with the `repo` scope. If the secret is missing, the run still
creates or updates the issue, logs a warning, and comments on the issue that
Copilot was not assigned. If the secret is present but assignment fails, the
job fails.

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

These source checks include the migration catalog maximum, live-history prefix,
runtime CONNECT, relation, and schema ACL checks used by
`--apply-pending-migrations`.

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

## Test platform component reporting

Run the report tests:

```powershell
dotnet test tests\Agentweaver.Providers.Tests\Agentweaver.Providers.Tests.csproj --filter FullyQualifiedName~PlatformComponentReportTests
dotnet test tests\Agentweaver.Projects.Config.Tests\Agentweaver.Projects.Config.Tests.csproj --filter FullyQualifiedName~PlatformComponentCollectorTests
dotnet test tests\Agentweaver.Projects.Config.Tests\Agentweaver.Projects.Config.Tests.csproj --filter FullyQualifiedName~PlatformComponentReaderRequiresCurrentTenantAdminOrPlatformAdmin
```

The first command checks the 16-area catalog, report composition, readiness
rules, and that the area list matches the table in
[Provider extensibility](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md).
The second checks parallel owner collection, the three-second owner limit, and
the owner HTTP client. The third needs Docker. It checks that only a current
TenantAdmin or PlatformAdmin can read the report. These tests don't call
deployed owner services.

## Planned GitHub Canvas adapter acceptance

GitHub Canvas adapter cases are unexecuted requirements, not a passing suite
or current adapter support. The Canvas research did not run interoperability, rendering, or restart tests.
The adapter is not implemented yet. Do not
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

The one-account P1 sign-in launcher also has a real browser regression that
uses loopback redirects only. Install the Playwright test browser and run it:

```powershell
npx playwright install chromium
npm run test:harness:signin-browser
```

The regression starts Chrome with a fresh empty profile under the ignored
`scripts/ui-harness/.auth/` directory and deletes it in `finally`. It checks
that a callback reached through server-side redirects is blocked before the
Portal document or script loads. The launcher waits for Chrome to abort each
intercepted callback before it validates the state or exchanges a code.
Invalid callbacks must also produce zero Portal requests. Closing the page
is not a substitute for aborting the request. The interception session stays
attached until the page closes, including when sign-in fails or is cancelled.
The shared contract tests delay the abort acknowledgement to check this
ordering without depending on browser timing.
It renders Broker consent when that response arrives through a redirect.
The shared harness tests cover `--surface api,mcp`, separate evidence paths,
one setup sign-in, and distinct run-bound tokens. These tests do not sign in
to Entra, use an existing profile or credential cache, or prove live SSO.
CI runs both the harness contract suite and this browser regression.

To check timing-sensitive changes, run the browser suite repeatedly, both
normally and with `NODE_V8_COVERAGE` set to a local output directory. Keep
the zero-request assertions unchanged and record the pass and failure counts
for each mode. Coverage instrumentation can expose cleanup races that do
not appear in a single run. These local runs do not prove live sign-in.

The runtime-observation probe also has a focused local test:

```powershell
node --test scripts\api-harness\test\runtime-observation.test.mjs scripts\azure\p1-dispatch-recovery-probes\tests\run.test.mjs
```

This read-only probe requires an already authorized client and real Environment
creation receipt. Stored rows alone do not prove dispatch, a late provider
callback, or verified resource release. The full harness runner checks source
and deployment evidence before it reads credentials or starts a surface
client. It does not sign in, refresh tokens, or grant authority. When the
publication receipt reuses an unchanged image, the deployment evidence checks
that image's registry labels against the commit that originally built it, not
the release commit. For setup
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
| Projects & Config | Real PostgreSQL project authority and revision transactions, including casting-generation proposal staging, rejection, stale revisions, concurrent completion, and idempotent replay. | A deployed owner service, a live AgentHost model run, or production model credentials. |
| Providers and resource selection | Catalog validation, cardinality and override rules, capability negotiation, immutable selection pins, meter-source compatibility, and adapter request contracts. | Resource provisioning, SDK model-source provenance, cloud access, pricing, or network-policy enforcement. |
| Orchestrator | Workflow grammar and step-DAG validation, bounded WorkPlans, schedule and event-predicate evaluation, backlog graph edits, readiness evidence, MAF capture checks, provider pins, Policy evaluation, action-guard receipts, and ordered Guardrails checks with a stubbed Prompt Shields transport. PostgreSQL cases exercise backlog edits, concurrent claims, restart replay, rollback on lost authority, immutable Guardrails evidence, request-bound coordinator-root model-turn admissions, and shared root/workflow turn limits. | Automatic workflow initiation, full child dispatch, live model output, deployed authorization, a live Content Safety resource, MAF gate wiring, or protected-effect call-site enforcement. A compiled PostgreSQL case is not database execution evidence. |
| Gateway and Identity | Broker-issued tokens, owner authorization fixtures, route contracts, status forwarding, current-grant checks, and reauthorization before event writes. | Deployed Gateway or owner availability, live OAuth, native TLS handshakes, live Kubernetes ownership, or an external identity-provider response. |
| AgentHost and model execution | The pinned SDK sends real test turns to an isolated loopback endpoint. Tests check bearer, model, request path, session marker, response receipt, and measured compaction under a bounded token limit. An owner-backed PostgreSQL test also runs an accepted coordinator-root generation turn through the persisted action grant. | Hosted Copilot, production BYOK access, paid model execution, or deployed runtime behavior. |
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
