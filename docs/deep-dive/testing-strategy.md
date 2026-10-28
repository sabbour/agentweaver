# Testing strategy

Use this page to choose the smallest test that proves a behavior at its
ownership boundary. Pure tests cover deterministic rules. PostgreSQL tests
cover transactions and concurrency. Controlled transports cover external
contracts. Deployed acceptance checks real source, identity, runtime, and
cleanup.

These results are not interchangeable. Compiling a PostgreSQL test does not
run it. A controlled browser-page test does not launch a browser. A successful
surface journey does not prove every native execution boundary.

## Find the test suites

| Location | Coverage |
| --- | --- |
| `tests/` | .NET package and service suites |
| `scripts/api-harness/` | Live OpenAPI discovery and controlled HTTP tests |
| `scripts/ui-harness/` | Injected-page observation and owner receipt checks |
| `scripts/mcp-harness/` | MCP transport, discovery, and tool-result tests |
| `scripts/harness-shared/` | Target/auth guards, journeys, redaction, and acceptance results |
| `apps/web/e2e/` | Playwright journeys against the built Web app with fake Broker and Gateway services, including Priority create/order, notes, conversation links, and reloads |
| `scripts/coverage/` | Coverage collection and receipt guards |
| `.github/workflows/v1-ci.yml` and `v1-docs-ci.yml` | Automatic source and documentation validation |
| Integration-image, release-pack, and continuation workflows | Artifact preparation and controlled publication |

## Choose the right test boundary

Ordinary tests do not require cloud credentials. Provider and identity tests
use in-memory state, fake HTTP/Azure transports, and local hosts. Source
Control tests use real isolated Git repositories for file behavior and
controlled GitHub responses for remote behavior.

Project-wide Knowledge discovery is covered by service boundary tests and
provider tests for project isolation and all-agent search. Web API and page
tests check that discovery needs no run or agent in the request path and that
records from distinct provider resources share one globally ordered page. The
returned access run authorizes revision-history reads; it is not record
provenance. These tests use controlled owner responses and provider stores;
they do not prove a deployed service.

Store suites use disposable PostgreSQL through Testcontainers, which runs
PostgreSQL in a container. These cases require Docker-compatible container
access. Broker browser integration uses Chromium through Playwright. Native
AgentHost tests use separately configured native runtime or image fixtures.

Do not replace a required database case with an in-memory provider and claim
the same evidence: row locks, advisory locks, transactional rollback, and
lease races require PostgreSQL.

The Orchestrator Core suite covers RubberDuck's deterministic contract and
recovery rules with pure tests, and exercises durable restart behavior with
PostgreSQL. The durable cases verify that an owner retry after restart reuses
the checkpointed operation and recorded response, that `REQUEST_CHANGES` stores
a failed receipt and blocks Merge, and that changed recorded evidence is
rejected before an owner call. These cases are not covered by the focused
non-PostgreSQL test filter.

The Broker migration and bootstrap suites use disposable PostgreSQL to
reproduce SQLSTATE `42501` on migration history, verify the production
migration/grant path, repair a missing grant on a no-pending-migrations rerun,
and reject history writes. They also confirm that read-only verification
does not repair missing grants. These cases do not change live permissions.

The Orchestrator store suites prove that the reviewed migration plan applies
032 with the rest of the Core catalog, preserves its first-applied coverage
time on replay, and rejects partial table-and-ledger state. Startup still
accepts a complete pre-032 schema during rollout; the list returns
`503 pull_request_records_unavailable` before the maintenance run and returns
stored create or reuse records after it. The full owner journey in
`CoordinationMessageIntegrationTests` checks the recorded pull request through
the native and Gateway routes.

Harnesses preserve HTTP errors, owner denials, and `202 Accepted` responses as
distinct from completed work. They record correlation and revision/fence
evidence. Redaction runs before durable JSON Lines output; transient raw
bodies are not saved as the transcript.

The P1 one-account launcher shares one fresh browser session across setup and
API/MCP run authorization. Each surface retains its own fixture and evidence
file. Its loopback Chrome regression checks redirect-hop callback blocking
before the Portal can consume the code, including an invalid state and
redirected Broker consent. Shared
contract tests check multi-surface execution and a callback that arrives while
navigation is still pending. These tests do not use Entra or prove live SSO.
See [Test retained API, UI, and MCP adapters](../guide/testing.md#test-retained-api-ui-and-mcp-adapters)
for the commands.

Authentication helpers check supplied token metadata and expiry. Broker,
Gateway, and resource services remain responsible for server-side signature
validation and current owner authorization.

## Run local validation

From the repository root, restore and build with the canonical commands:

```powershell
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
```

Then run the affected test project. For example, the
[testing guide](../guide/testing.md) includes this Source Control suite command:

```powershell
dotnet test tests\Agentweaver.SourceControl.Tests\Agentweaver.SourceControl.Tests.csproj --no-build --no-restore --configuration Release
```

Run the controlled harness contract suites without contacting a deployed
environment:

```powershell
npm run test:harness:api
npm run test:harness:ui
npm run test:harness:mcp
```

The API suite uses controlled loopback HTTP. The UI suite uses an injected-page
contract. The MCP suite includes a real SDK connection to a controlled
loopback JSON-RPC HTTP server. Locked dependencies live under
`scripts\mcp-harness`.

## Understand API, UI, and MCP journeys

The API client discovers `/openapi/v1.json` and invokes advertised `/api/v1`
operations. It rejects unknown operation IDs, extra parameters, cross-origin
requests, and redirects.

The UI adapter accepts an already-authorized page on the exact Web origin. It
observes real Gateway request/response shapes and validates tenant, actor,
gate, and owner receipt bindings. It does not sign in or seed browser storage.

The MCP adapter uses Streamable HTTP at `/mcp`. It reads all tool-discovery
pages, rejects duplicate or foreign tools and repeated cursors, and checks the
structured owner result. It preserves `isError`; closing the client does not
release Environments or archive projects.

`npm run harness:p1` runs the shared surface entry point when you provide
explicit target and authorized runtime inputs. Its result separates
`surfaceStatus` from `fullP1Status`. Required deployment-source,
image-measurement, and native probes remain blocked until evidence exists.
The aggregate exits `0` for passed, `1` for failed, and `2` for blocked.

For one-account P1 API or MCP checks, `npm run harness:p1:signin-run -- ...`
uses the existing `agentweaver-web` client with one interactive sign-in in a
fresh empty disposable Chrome profile. It removes the profile in a `finally`
path and does not read or copy an existing profile or credential cache. Tokens
remain in memory only. The launcher does not sign in a denied actor. The input
manifest must explicitly set `executionIntent` to `"execute"`; a missing or
different value is rejected before the harness contacts the target. If
`fixturePlan.deniedActor` is omitted, `valid-denied-actor-403` remains blocked
with the missing-account reason, the other surface checks continue, and the
surface cannot pass. Distinct-actor coverage is unavailable to this one-account
operator flow; no same-user substitute or role change is permitted. A
lower-level identity provider can preserve it only when a separate actor
credential is independently available. The Host-caller probe-provider gap is
still blocked; the launcher does not supply Host-caller probe adapters.

The v1 UI surface remains blocked until the 0.x UI is restored onto v1. Do not target the current
`apps/web/src/v1/App.tsx` pages with new UI selectors or page providers.

Pass the exact native and dispatch-recovery output envelopes with
`--native-probe-envelope` and `--dispatch-recovery-envelope`. The aggregator
preserves every supplied receipt and blocks any missing required probe. It
rejects malformed, inconsistent, duplicate, and extra envelope fields rather
than silently dropping them. It also verifies each envelope's canonical
`receiptSha256` checksum to detect changes after writing. Use output from live
probe calls; test fixtures and scripted results are not live evidence. The
checksum does not authenticate who wrote a file. A probe cannot claim `passed`
with empty evidence; native passes also require completed or not-required
cleanup, and read-only dispatch-recovery passes must report no created
resources.

The [native probe CLI contract](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/p1-native-probes/README.md#explicit-native-cli-and-provider-module)
documents the explicit `--execute` entry point and trusted owner-provider
module. Native CLI execution currently exits blocked at `native_authority_join`:
the trusted owner provider now joins the authenticated Core registration and
invocation binding, Environment creation and Pod observations, and Host
readiness/source evidence. Its locator-only Host selector can report one
current fixed-credit guard/no-send pair, but does not supply Host
downward-API self-identity or the runner's broader turn, cursor, effect, and
dispatch proof slots. Registration `placementUid` identifies the SandboxClaim
resource, independently of the actual Pod UID. Controlled runner regressions
reject substitution and replacement of either identity; they do not establish
live authority, Pod identity, or acceptance. The CLI does not supply live owner
adapters, create fixtures, sign in, or turn controlled tests into live evidence. Its
focused suite runs without Azure or Kubernetes access:

```powershell
node --test scripts\azure\p1-native-probes\*.test.mjs
```

The P1 runner accepts two deployment evidence paths. An images-only integration
publication requires its saved integration deployment receipt and the current
checkout at the exact source commit. A published release uses the exact
`releaseTag` and source manifest, verifies the tag and immutable publication
result against GitHub, then reads the selected namespace and verifies each live
image digest and its source-commit and source-tree labels. Release evidence can
use a newer clean helper checkout because the manifest and project files are
read from the exact source commit in Git.

The bootstrap path accepts only the tracked draft at its published source
commit. It derives a digest-pinned deployment manifest from the verified
publication receipt and records the result as `pre-acceptance`; it does not
claim that the bootstrap command created release-acceptance evidence. Both
release paths hash the publication and current resource readbacks together.
Release and bootstrap inputs require `releaseTag` and omit
`integrationDeploymentReceiptPath`. Images published without the required
source-tree label fail this check; immutable images cannot be relabeled.

Native probes cover Storage isolation, network datapath, Sandbox lifecycle,
AgentHost readiness, numeric dispatch limits, and restart/duplicate-fence
recovery. The dispatch-recovery reader can query existing Host no-send receipts
and a current replacement Host recovery receipt, but it does not create a
dispatch or replace a Host. Numeric-boundary, soft-warning, and complete
gate/join no-duplicate receipts remain blocked until source owners expose all
required evidence. A policy-object readback or usage query cannot replace those
probes.

## Treat coverage as an artifact

`npm run coverage:dotnet` runs configured .NET suites against the Release build
with TRX and Cobertura collection. It requires nonempty case receipts, one
distinct coverage report per suite, and combined reports with expected
production assemblies. It restores the repository-local report tool and writes
under `artifacts/coverage/dotnet/`.

`npm run coverage:node` runs release and Azure tooling tests with Node
coverage. It writes LCOV and a text summary under
`artifacts/coverage/node/` and requires executed release-validator lines. It
does not measure every Node file or every harness. The Web package has its own
coverage command. A missing report fails its guard; a present file alone is
not sufficient evidence.

## Understand CI boundaries

`v1-ci.yml` runs on `v1` pull requests, pushes and manual dispatches. Its
always-reported `foundation` aggregate gates independently running image,
Web, Node and .NET jobs. Path classification selects expensive suites; release
composition, controlled P1 journeys and CI contract checks remain mandatory.
The image job retains source-bound Probe, Broker and AgentHost image checks,
the pinned native runtime and isolated native prompt-capacity tests.

.NET projects run concurrently, with four deterministic method shards for
Broker, balanced by committed measured method durations, and verified
per-shard TRX receipts. NuGet, Chromium, and exact-key
installed web dependency caches avoid
repeated downloads and installs, without caching mutable databases, browser contexts, or build output.
PR, push, and manual CI runs never collect coverage. The weekly
`v1-coverage.yml` workflow reuses the same jobs with instrumentation, combines
the reports without a second pass, and publishes separate .NET, Web, and Node
line-coverage badges plus one Copilot coverage-improvement issue. Each suite
artifact is bound to its run attempt, so after a failed-jobs rerun the combined
report uses only the newest passing attempt of each suite.
The [testing guide](../guide/testing.md#understand-v1-pull-request-ci)
describes path dependencies, cache invalidation, retained skip behavior and
the measured performance acceptance criteria. Its
[coverage section](../guide/testing.md#measure-v1-coverage-weekly) covers the
schedule, denominators, attempt binding, and one-time Copilot token setup. Controlled journeys
are not automatic deployed acceptance.

The `go` job runs `gofmt`, `go vet`, and `go test` for the Go reference tools in
`tools/agent-primitives`. They produce the AgentSessions fixture that the .NET verifier tests check, so CI compares both implementations on every pull request.

`v1-docs-ci.yml` builds VitePress, checks links and source coverage, and
validates diagram provenance and readability. On a `v1` push it stages a
source-bound site artifact.

Integration-image and release-pack workflows are manual artifact workflows,
not substitutes for test CI. Preparation and publication are separate jobs.
The partial-publication workflow admits only its exact recovery dispatch and
missing artifacts; it does not rerun the general test strategy.

## Validate documentation changes

From the repository root, run the same local checks used by docs CI:

```powershell
npm run docs:build
npm run docs:check-links
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
```

Update affected public explanations with behavior and contract changes. Keep
source mapping and diagrams aligned with the implemented owner boundary, not a
proposed future service.

## Related content

- [Test commands and suite boundaries](../guide/testing.md)
- [Environment suspend testing](../guide/environment-sandbox-testing.md)
- [Release reference](../reference/releases.md)
- [Infrastructure and deployment](./infra-deployment.md)
