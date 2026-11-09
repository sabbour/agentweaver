# Testing strategy

## 1–4. Goals, constraints, context and strategy

Use the smallest test that observes a behavior at its ownership boundary.
Deterministic rules, database concurrency, native SDK contracts and deployed
acceptance need different evidence. Passing one layer does not establish
another: compiling PostgreSQL tests does not run them, and a fixture is not a
live provider.

This view explains the verification architecture. Use the
[testing guide](../guide/testing.md) for executable commands and prerequisites.
Do not run live journeys against an unapproved target or replace a required
PostgreSQL race with an in-memory implementation.

The strategy keeps fast pure tests near domain rules and uses the real local
boundary where semantics depend on Git, PostgreSQL, native runtime or browser
behavior. Controlled external transports make failures reproducible without
claiming production credentials or service availability.

## 5. Building blocks

| Location | Responsibility and evidence boundary |
| --- | --- |
| `tests/` | .NET package/service tests; disposable PostgreSQL where transactions and locks matter |
| `scripts/api-harness/` | OpenAPI discovery/client contracts and controlled HTTP |
| `scripts/ui-harness/` | Injected-page observation and owner receipt contracts |
| `scripts/mcp-harness/` | Retained compatibility transport, discovery and result tests |
| `scripts/harness-shared/` | Target/auth guards, redaction, journeys and result contracts |
| `tools/Agentweaver.Cli/` | Native .NET CLI API acceptance entrypoint |
| `apps/web/e2e/` | Playwright journeys against built Web with controlled Broker/Gateway |
| `scripts/coverage/` | Coverage execution, report validation and receipt guards |
| `.github/workflows/v1-ci.yml` | Source validation aggregate and selected suites |
| `.github/workflows/v1-docs-ci.yml` | Site, link, source-map and diagram checks |
| `.github/workflows/v1-integration-images.yml` | Direct GHCR image build/publication, not acceptance |
| `scripts/release/deploy-kustomize.mjs` | Migration-first apply and rollout checks, not product journey proof |

## 6. Runtime view

### Match the stimulus to the boundary

| Contract under test | Representative stimulus and observation | Not established by that test |
| --- | --- | --- |
| Pure workflow/provider rules | Invalid graph, cardinality, version or capability returns structured rejection | A database commit or available backend |
| MAF dispatch | Production catalog binding and child intent/outbox/checkpoint transactions against disposable PostgreSQL; controlled Broker composition holds the independent-review gate until two `RuntimeAgentHost` children join | Native AKS execution, live owner services or paid model calls |
| Owner transactions | Concurrent claims, stale revisions, lost authority and restart replay against PostgreSQL | Deployed service behavior |
| Source Control | Real isolated Git workspaces; controlled GitHub HTTP, expected heads and raw-byte HMAC | Live GitHub permission or merge approval |
| Broker/grants | Concurrent refresh redemption, exact-purpose checks and revocation after waits | Live upstream SSO or Key Vault access |
| AgentHost | Pinned native SDK against controlled loopback endpoint; inspect bearer/model/path and emitted evidence | Hosted Copilot or paid BYOK acceptance |
| Browser | Built application with controlled services or injected page contract, depending on suite | Production sign-in or a different browser journey |
| Deployment tooling | Manifest inputs, local renders and controlled transports | Actual target permissions, rollout or native resource behavior |

For example, RubberDuck durable tests restore checkpointed operation and
response after restart, preserve `REQUEST_CHANGES` as failure, and reject
changed evidence before an owner call. Pure tests alone cannot establish that
restart behavior.

Broker database tests reproduce SQLSTATE `42501`, exercise the canonical
migration/grant path, reject history writes and verify that read-only checks
do not repair grants. Projects initialization tests verify dedicated-owner
access before writes and rollback on reconciliation failure.

Orchestrator store tests exercise migration 032, its first-applied coverage
time and partial-state rejection. A complete pre-032 schema remains valid
during rollout; the project list returns
`503 pull_request_records_unavailable` until maintenance applies the migration.

Knowledge discovery tests verify project isolation, all-agent search, one
global order across resources, and history access through `accessRunId`.
They also verify that project-wide search requires `AccessPrivateKnowledge`
but not `ReadRunSelection`. That access binding is not `sourceRunId` provenance
or deployed discovery.

### Separate canonical acceptance from compatibility tests

Canonical API acceptance runs the native .NET apphost with
`agentweaver test --input <manifest> --output <new-evidence-file>`. Use the
source-published apphost or exact extracted distribution executable for the
question being tested. The Node `harness:p1 -- --surface api` path delegates
to it through `AGENTWEAVER_CLI_PATH`; it does not fall back to a Node API runner.

The manifest selects the target. `--interactive` explicitly permits browser
authorization; a live journey creates and cleans up fixtures and requires a
separately approved non-production target. Successful `--dry-run` performs no
network/authentication/browser work and writes no evidence, even when
`--interactive` is present. Validation failure may write a blocked result.
See the [CLI guide](../guide/cli.md) and [testing guide](../guide/testing.md)
for the full input, target and execution contract.

The `full-flow-delivery` journey is the critical-path acceptance journey. It
uses a real BYOK model and checks outcomes, not exact model text. The file
`tests/fixtures/critical-paths.json` maps the journey to its watched Gateway
routes and known gaps. `CriticalPathCoverageTests` fails pull request CI when
an operation is added under a watched prefix without a classification, when a
route arrives for a known gap, or when a covered operation is removed. See [Keep critical-path coverage current](../guide/testing.md#keep-critical-path-coverage-current).

The retained `/mcp` harness exercises compatibility code only. It preserves
`isError`, validates discovery pages/cursors, and distinguishes accepted from
completed results. It is not a supported product or acceptance surface;
the API entrypoint rejects `--surface mcp` and `--surface api,mcp`.

The UI contract uses an already-authorized page on the exact Web origin. It
does not sign in or seed browser storage. A fake-service Web end-to-end test
and an injected-page test must not be reported as a live UI journey.

### Preserve evidence and blocked outcomes

Harnesses keep HTTP errors, owner denials and `202 Accepted` separate from
completion. Redaction precedes durable JSON Lines output; transient raw bodies
are not the transcript. Token helpers check metadata/expiry; servers still
validate signatures and current authority.

The controlled Board-delete regression passes the owner-denial step only for
HTTP `403` and an owner JSON `error` field equal to
`backlog_project_permission_denied`; evidence retains that value as
`ownerIssueCode`. A generic Gateway problem with `code: "forbidden"`, a
bodyless `403`, or a changed authoritative graph is not a passing owner-denial
result.

The controlled `session-history-metric` regressions cover both raw-context
selection and interactive pending-run creation through the CLI runner. They
check server-returned identities and selection revisions, forbid start and
dispatch operations, and verify cancellation, advanced-fence retained-session
readbacks, and project cleanup order. Lost create responses, malformed
selection revisions, and failed cancellation must remain indeterminate rather
than report the fixture absent or cleaned. These local tests do not establish
live authorization or deployed owner behavior.

Native evidence must join registration, invocation binding, Environment
creation, actual Pod/mount observations and Host evidence. Registration
`placementUid` is the SandboxClaim identity, not the actual Pod UID. A policy
readback, readiness receipt, usage read or locator-only no-send result cannot
replace complete turn, cursor, effect and dispatch/recovery evidence.

The `native-material-recovery` journey joins the current root registration,
turn-completion and material receipts, ordinary Host replacement, exact replay,
and cleanup. It can pass the session-material row only when these bound
receipts validate; it does not pass full-P1 acceptance or prove deployed
provider behavior.

Native and dispatch-recovery envelopes preserve supplied receipts and reject
malformed, inconsistent, duplicate or extra fields. `receiptSha256` detects
content changes, not authorship. `passed` requires nonempty evidence; native
cleanup must be completed or not required, and read-only recovery must report
no created resources. Missing required evidence stays blocked.

Distinct-actor denial requires a genuinely distinct authorized test identity.
Do not substitute the same user, a temporary role change or an invalid locator.
A one-account flow does not establish full denied-actor coverage.

Artifact/source and deployment evidence remains separate from the product
journey. Follow the [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
for current publication/deployment operations; do not infer acceptance from
their success or introduce an image archive/publication-receipt handoff.

## 7–9. Execution environments, shared concepts and decisions

Pure and controlled tests run locally or in CI. PostgreSQL cases require
Docker-compatible Testcontainers; Broker browser integration requires
Playwright Chromium; native tests require the configured pinned runtime/image.
These dependencies are test environments, not a product deployment topology.

`v1-ci.yml` reports the `foundation` aggregate across selected image, Web,
Node and .NET jobs. Path selection limits expensive suites without turning
skipped work into evidence. Broker method shards retain per-shard TRX
receipts; caches hold dependencies, not mutable databases, browser contexts
or build output. Go reference tests under `tools/agent-primitives` produce
AgentSessions fixtures compared with the .NET verifier.

Coverage is an artifact, not a passing-test count. .NET collection requires
distinct Cobertura/TRX receipts and expected assemblies; Node coverage requires
executed release-validator lines. Reports live under
`artifacts/coverage/dotnet/` and `artifacts/coverage/node/`. Weekly
`v1-coverage.yml` instrumentation is separate from ordinary PR/push/manual CI.
Combined reports retain the newest passing attempt for each suite.

Read [CI and coverage details](../guide/testing.md#understand-pull-request-ci)
for the canonical execution contract. The
[system decision view](./00-system-overview.md#9-architecture-decisions)
retains actual ADR statuses; tests do not accept a proposal or establish a
CLI/API compatibility policy.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| A regression changes an offending artifact or contract | Focused assertion fails on its content, identity or state transition, not only exit code | Test-design criterion; inspect the actual assertion |
| A job produces no expected test/coverage receipt | Guard fails rather than count an empty run as passing | CI/coverage contract tests |
| A live acceptance prerequisite or cleanup result is missing | Preserve failed/blocked status; do not aggregate into pass | Harness/CLI result tests; complete model-dependent live acceptance is separate |

The principal risk is overclaiming evidence. Source owners must identify the
exact revision, executable, environment, executed suite and limitations.
Controlled transport and fixture success do not establish native TLS, packet
enforcement, mounted persistence or paid-provider behavior.

**Terms:** controlled means dependencies are supplied by the test;
integration means real participating boundaries within the named environment;
acceptance means the specified journey and all required evidence passed.
Compiled, executed, released, deployed and proved are separate states.

## Validate documentation changes

From the repository root, use the existing safe checks:

```powershell
npm run docs:build
npm run docs:check-links
npm run docs:check-drift -- --base origin/v1
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
npm run test:docs-diagrams
```

These inspect site/artifact consistency, not technical correctness of every
claim. Source review remains necessary.

## Related content

- [Test commands and suite boundaries](../guide/testing.md)
- [Environment suspend testing](../guide/environment-sandbox-testing.md)
- [Release reference](../reference/releases.md)
- [Infrastructure and deployment](./infra-deployment.md)
