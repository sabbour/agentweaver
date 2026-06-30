
## 2026-06-07: Comprehensive test suite — 43/43 passing

Delivered: tests/Scaffolder.Tests/ with 43 tests across 7 files. SandboxPathValidatorTests (9: absolute, traversal, .., symlink/junction, null-byte, empty — all rejected), EventTypeTests (4), SqliteEventStoreTests (6: append-only trigger, monotonic sequence), RunEventBroadcasterTests (5: replay, dedup, fan-out), ApprovalGateTests (5: state machine with real git repo + merge), ModelSourceValidationTests (6: provider validation), ContentSafetyCheckerTests (8: null bytes, credential patterns). No mocks or fakes — TestSqliteDb creates unique temp database per test; ScaffolderWebApplicationFactory injects real API key and paths. Program.cs decorated with `public partial class Program` to enable WebApplicationFactory access. All 43/43 passing.


## 2026-06-10 — Tool-layer test obligations for spec 002

The sandboxed execution plan now includes a reusable `Scaffolder.AgentTools` layer and custom sandboxed replacements for Copilot built-ins. Future tests must cover both runners registering the same canonical tool set: `run_command`, `read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit`, `store_memory`, `vote_memory`, `update_todo`, `report_intent`.

Important test requirements from reviews: `apply_patch` must reject path escapes in any hunk or `Move to` destination with zero writes; all tool return values must be redacted before model return, not only event logs; `RunCommandTool.IsOverride` must be false; canonical tool names must be asserted against a hardcoded expected array; T049 needs coverage across file, search, and internal tool branches; CLI/Web should expose `agent.intent` status.

## 2026-06-10 — Spec 002 unit tests (T026-T029, T051-T052) — IN PROGRESS at session log time

Test scope for this session:
- T026-T029: `SandboxExec` unit tests (`WindowsNativeSandboxExecutor`, `Wsl2SandboxExecutor`, `LinuxNativeSandboxExecutor`, `SandboxExecutorFactory`)
- T051-T052: `Scaffolder.AgentTools` unit tests (`SandboxedFileTools` — 5 tools; `SandboxedSearchTools` — 2 tools)

Critical test obligations carried forward from review findings:
- `apply_patch` must reject path escapes in any hunk or `Move to` with zero writes
- Tool return values must be redacted before model return
- `RunCommandTool.IsOverride` must be false
- Canonical tool names asserted against hardcoded expected array (T049/RBD-B4)
- T049 covers file, search, and internal tool backend branches

## 2026-06-11 — 003-projects test suite + security review (Phase 7)

Wrote comprehensive .NET tests for 003-projects: `ProjectsControllerTests`, `SqliteProjectStoreTests`, `TryBeginDeleteAsyncTests`, `TryCreateProjectRunAsyncTests`, `GitHubTokenStoreTests`, `ProjectWorkspaceProviderTests`, and full `ProjectsWebApplicationFactory` integration tests. Total suite: 395 .NET / 106 Vitest tests passing. Security review: 8/8 items clean (CAS atomicity, OAuth token storage, token scope isolation, sign-out fail-closed, path validation, input validation, test isolation).

**Learnings:**
- `ProjectsWebApplicationFactory` pattern: extend `ScaffolderWebApplicationFactory` and inject `NoOpProjectGitInitializer` + `InMemoryGitHubTokenStore` via `ConfigureTestServices` for full API integration tests without live git or OAuth.
- `InMemoryGitHubTokenStore` for test isolation: pre-seed per test class; clear in `IAsyncLifetime.DisposeAsync` to prevent cross-test token leakage.
- Concurrency test pattern with `Task.WhenAll` + `Task.Delay(50)`: interleave two CAS-contending tasks; assert exactly one succeeds and the other returns the expected failure result (not an exception).

## 2026-06-11 — 003-projects implementation plan race-fix (Round 3)

Smith delivered surgical round-3 fix for specs/003-projects/plan.md after Morpheus locked out by rubber-duck TOCTOU finding. Implemented atomic `TryCreateProjectRunAsync` reservation: run insert guarded by project.Active in single SQLite transaction, eliminating split-transaction race between delete and run-create. Rubber-duck found new blocker: reserved Pending row leaks if post-reservation side effect fails. Folded in 3 polish items (NeverSignedIn scope, token keying, Phase 1 ordering). Smith locked; Link assigned round 4 compensation fix. Contribution: race-safe reservation; narrowed gap to compensable post-reservation failure.

## 2026-06-12 — Feature 005 test suite (SC-001–SC-007)

Built `CastingWebApplicationFactory`, `SquadTestFixtureHelper`, and 9 test files covering SC-001–SC-007 for the Agent Team Casting feature. Turn 1 used `#if SQUAD_AVAILABLE` compilation guards because Scaffolder.Squad was not yet referenced by the test project. Turn 2 removed all guards and fixed API mismatches after Tank wired the project reference; fully activated `UniverseAllocatorTests`, `CatalogReaderTests`, and `SquadReaderWriterTests`. Build: 0 errors. Final suite result: 418 passed, 14 skipped (expected), 0 failed.

## 2026-06-12 — Feature 005 integration test activation

Activated ScenarioCastingTests, TeamManagementTests, SyncTests. Feature 005 test suite: 430 passing, 0 failed, 14 skipped (expected). Build clean. Committed as 3053741.

## 2026-06-17: Feature 008 Phase 1 integration tests: 13/13 passing

Built CoordinatorWebApplicationFactory (real in-process API host + real SQLite + real CoordinatorRunService/CoordinatorWorkflowFactory/MAF request-port suspend-resume, mirroring ReviewWebApplicationFactory). Used SignedOutGitHubTokenStore to keep coordinator drafting hermetic (no network calls; deterministic built-in draft). Wrote 13 real integration tests covering: start draft + persist + emit + suspend (no dispatch), confirm on pending gate (200, status confirmed, gate consumed), confirm edge cases (inactive run → 409 run_not_active; drained gate → 409 NoPendingGate; unknown run → RunNotActive), double-confirm (409 no double-consume), revise (re-draft + re-suspend), owner-scoping (non-owner → 403), error cases (404/400). Repaired pre-existing test-compile break in WorkflowRestartServiceTests.cs (missing SqliteRunStore and IServiceScopeFactory args in call sites; test-only, no product code modified). Result: 13/13 new tests pass, build clean (0 warnings, 0 errors), 426/445 total passing (17 pre-existing environment-dependent failures).
📌 Team update (2026-06-22): Integrated verification for commits b7282cf4 / 877ebf59 / 77255271 was all green: backend 728 passed/2 skipped and web 267 passed — verified by Smith.
📌 Team update (2026-06-26T09:37:26-07:00): Seraph is designing the MCP OAuth 2.1 flow for the AKS-hosted Agentweaver MCP server (Option C: Copilot CLI token auto-refresh). Implementation tasks and owner breakdown are expected after the design lands in the decisions inbox.

## 2026-06-26T09:37:26-07:00 — MCP OAuth 2.1 QA pass complete

Smith produced S1-S5 MCP OAuth scenarios and reported 22 passing / 29 skipped in commit 39394a6. GAP-1 through GAP-6 remain recorded as follow-up QA/environment gaps while the build is otherwise documented as merge-ready.

## 2026-06-27T02-23-10 — Sandbox Validation: Activation Paths + Coverage Gap Report

Validated the preview sandbox and mapped all activation paths for the pod-per-run architecture investigation. Governance layer: ✅ 151/151 tests pass. Production executor (`KubernetesSandboxExecutor`): ❌ zero test coverage. `SandboxEscapeEndToEndTests`: ⚠️ silent false-positives — 2 tests return early with zero assertions when `RUN_LIVE_PROVIDER_TESTS=1` is unset. Windows devs: `PassthroughExecutor` (no real isolation). Most executors (Mxc, bwrap, Kubernetes, PortForward) are untested in CI.

**Option A adopted.** Required new coverage documented in `decisions.md`: fake IKubernetes exec-channel double, broker decision tests (allow/deny/fail-closed), token tests (audience binding, TTL, replay rejection), fix false-positive escape tests to use `Assert.Skip`, gated live e2e pod escape proof.


## 2026-06-27T03:05:00-07:00 — Spec 018 convergence

Spec 018 locked the architecture Smith should validate: sandbox-all-agent-execution, no broker, Azure PostgreSQL Flexible Server, and web/worker scaling with durable leasing. Test planning should focus on pod-hosted agent execution, MAF bridge behavior, leasing correctness, migration safety, and regression coverage for coordinator dispatch/double-dispatch risks.


## 2026-06-27T07:13:00-07:00 — Spec 018 PostgreSQL integration validation
Smith added the Testcontainers `postgres:16` integration suite for Spec 018. Result: 26 passed, 1 skipped. The suite found the migration-discoverability production bug that Tank fixed before final validation.

📌 Team update (2026-06-28T05:10:00-07:00): Smith re-reviewed Tank's preview fix (`298dc45`) after the B1 rejection and approved. The feature shipped to main (`373f544`) and deployed with `SANDBOX_PREVIEW_ENABLED=true`; B1 was the per-process `PodNameRegistry` at replicas:2, fixed via SandboxClaim resolution. Live AKS has no Istio CRDs.
