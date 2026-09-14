## Result

**The 28-item ownership manifest is usable, with qualifications below.** Retain means semantic retention—not visual approval or proof that tests passed. I read the reports and targeted current source/config/tests; I did not run tests, services, APIs, agents, or investigate pipelines.

**Ownership conflict:** the detailed audit lists **30 diagrams**, whereas the downstream manifest owns **28**. The additional entries are `assistant-runtime-fig1` and `canonical-durable-event-stream-sequence`. Their required corrections are documented, but ownership must be reconciled before editing either asset.
Sources: [manifest:4–34](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json#L4), [audit:820–823](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/deep-dive-core.json#L820).

## 1. All 28 owned dispositions

The line column identifies the exact disposition entry in [the detailed audit:808–837](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/deep-dive-core.json#L808).

| Owned diagram | Disposition | Replacement / qualification | Audit line |
|---|---|---|---:|
| `00-system-overview-fig1` | Redesign | Explicit MCP host; separate API, worker, AgentHost | 808 |
| `00-system-overview-fig2` | Retain | Full single-agent pipeline, **not current public run-submission flow** | 809 |
| `00-system-overview-fig5` | Retain | Observation → promotion → export feedback | 810 |
| `00-system-overview-fig7` | Reuse; retire original | `canonical-aks-components`, after owner reconciliation | 811 |
| `00-system-overview-fig8` | Merge; retire original | `canonical-durable-event-stream-sequence`, **after polling correction** | 812 |
| `agent-definition-fig1` | Redesign | Five generator outputs and check-mode coverage | 813 |
| `agent-framework-fig1` | Retain | Representative typed adapters, not exhaustive logical workflow | 814 |
| `agent-framework-fig2` | Retain | MAF → relational/service orchestration handoff | 815 |
| `agent-framework-fig3` | Redesign | Normal response vs restored checkpoint vs SQLite file fallback | 816 |
| `api-core-fig4` | Redesign | Endpoint-classified authentication/authorization | 817 |
| `api-core-fig6` | Retain | Narrow system-diagnostic check map | 818 |
| `api-core-fig7` | Merge; retire original | Prose link to `#the-host-in-one-picture` / `canonical-api-host` | 819 |
| `canonical-api-host` | Redesign | Separate startup from requests; absorb handler responsibilities | 821 |
| `canonical-testing-boundary` | Retain | Dependency-choice principle, not “all network is always fake” | 823 |
| `data-persistence-fig1` | Redesign | Production Postgres vs local split SQLite | 824 |
| `frontend-fig1` | Retain | Representative component/projection map | 825 |
| `frontend-fig2` | Redesign | Current routes; global vs project-relative paths | 826 |
| `frontend-fig3` | Redesign | External docs; origin-only runtime API configuration | 827 |
| `frontend-fig6` | Redesign | Entra sign-in/session exchange | 828 |
| `frontend-fig7` | Retain | Browser transport, separate from server event transport | 829 |
| `mcp-server-fig2` | Redesign | Consent, exact resource, required scope, current token state | 830 |
| `project-generation-model-settings-fig1` | Retain | Model preferences—not credential/provider authorization | 831 |
| `project-skills-fig1` | Redesign | Materialize-before-pointer; inline fallback; best-effort cleanup | 832 |
| `projects-fig1` | Retain | Workspace-provider provisioning/write probes | 833 |
| `projects-fig2` | Retain | Resource relationships, not execution/auth sequence | 834 |
| `repo-blueprint-suggestions-fig1` | Redesign | Anonymous metadata reads; cancellation ≠ fallback | 835 |
| `testing-strategy-fig1` | Redesign | Independent coverage layers, not an execution chain | 836 |
| `testing-strategy-fig4` | Retain | Factory configuration/substitution/request sequence | 837 |

**Totals:** 12 retain, 13 redesign, 3 replacement/consolidation dispositions. The three consumer migrations—including adjacent provenance-comment changes—are specified at [manifest:289–327](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json#L289).

## 2. Plan-owned prose, tables, and inline changes: all 15 documents

These are **planned editorial changes**, not assertions that the current prose already matches implementation.

| Document | Required non-asset changes |
|---|---|
| [System overview](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/00-system-overview.md#L45) | Explain Postgres polling; scope full-run lifecycle; preserve recovery-owner and vocabulary tables; distinguish Operator conversations and provider/charter roles. Keep shared coordinator/default-workflow/sandbox references. |
| [Agent definition](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/agent-definition.md#L45) | Correct three-output wording to five; preserve handwritten/generated split, non-clobbering materialization, selected filesystem-failure tolerance, embedded-copy drift tests. |
| [Agent framework](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/agent-framework.md#L5) | Remove “every run/every human gate is MAF/RequestPort”; explain Operator and collective-gate exceptions, remote worker leaf, ordinary response vs recovery; retain checkpoint schema table. |
| [API core](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/api-core.md#L183) | Replace token/org path gates with actual middleware order; retain endpoint-specific bind/lookup/role/delegate/DTO prose. Correct production persistence/auth tables; distinguish narrow diagnostics from detailed health. Add provider-admission explanation; proposed graphic remains unnamed/unowned. |
| [Assistant runtime](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/assistant-runtime.md#L32) | Correct Completed revival to Idle wake; distinguish default 5-minute pod release and 30-minute parking; retain bounded 24-message rehydration and durable accounting. Explain per-turn broker issuance/renewal, fresh SDK sessions, MCP-only tools, disabled SDK session store. |
| [Auth/security](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/auth-security.md#L3) | Retain Entra/platform/project role lists and separate GitHub capabilities. Link to corrected MCP sequence; retain one-shot AgentHost payload/guardrails and execution-owned references—no duplicate broad auth diagram. |
| [Data persistence](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/data-persistence.md#L53) | Migrate both inline models: optional project/workflow-run participation; project-scoped composite uniqueness; owning-subtask vs dependency edges. Correct child worktrees, local-only two-file migrations, OAuth state, event implementation path and unconditional live-channel wording. Preserve fenced memory-as-data explanation. |
| [Frontend](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/frontend.md#L128) | Correct repeated `/api`-base advice: API_URL is origin-only. Preserve state lifetimes, bounded buffering/reconnect and content-as-data prose; update Entra bootstrap/session wording and current route distinctions. Keep shared snapshot/coordinator references. |
| [MCP server](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/mcp-server.md#L1) | Remove GitHub-platform-login/org-admission/static-client-key implications. Retain HTTP/stdio/API-authority prose, tool-contract table/generated index, and three routing cases. Explain consent and current OpenIddict/token-family semantics. |
| [Projects](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/projects.md#L59) | Redesign inline lifecycle: persisted Active/Deleting vs conceptual phases/computed availability. Remove direct-URL creation and retired picker endpoints; explain caller-bound selection code, role-specific operations, deletion-before-cleanup, and live workspace probing. |
| [Generation model settings](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/project-generation-model-settings.md#L1) | Preserve three preference fields and reset/inheritance details; link to API admission. Saved model IDs neither choose credentials nor bypass provider fences. |
| [Project skills](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/project-skills.md#L11) | Preserve source forms, allowlist, hash/no-op and Missing/Malformed rules. Add confirmed-team preview → assignment plan → digest-checked apply. Qualify delivery and stale cleanup as below. |
| [Repository blueprint suggestions](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/repo-blueprint-suggestions.md#L19) | Preserve deterministic suggestion vs model generation distinction and UI fallback. Correct ambient-token claim and account-picker discussion; caller cancellation propagates. |
| [Testing strategy](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/testing-strategy.md#L203) | Redesign inline fake-run state model as a **scenario**, not production state machine. Retain factory and real/fake explanations, but correct obsolete auth/skipped-OAuth claims; add frontend, Postgres and real-process boundaries. Do not claim every workflow test traverses production execution. |
| [Index](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/docs/deep-dive/README.md#L1) | Keep navigation-only prose; replace GitHub/API-key platform-auth and SQLite-only descriptions with Entra/broker roles and production Postgres. No new diagram. |

Exact plan locations: [overview/definition/framework:9–203](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/deep-dive-core.json#L9), [API/assistant/auth:211–361](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/deep-dive-core.json#L211), [persistence/frontend/MCP:369–574](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/deep-dive-core.json#L369), [projects through index:582–802](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/.github/skills/docs-diagram-audit/reports/deep-dive-core.json#L582).

## 3. Source-backed boundaries and invariants

### Testing/factory/run-state visuals

- **Retain factory sequencing, not historical auth credentials as current architecture.** The workflow factory installs configuration and replaces `IAgentRunner`/`IWorkflowAgentFactory`; it also still contains legacy auth/bypass settings. These are fixture particulars, not production authentication documentation.
  [WorkflowWebApplicationFactory.cs:34–82](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Helpers/WorkflowWebApplicationFactory.cs#L34)

- **The deterministic agent is only a seam:** real file write, no-change result, or synthetic content-safety exception. It does not prove live model behavior. Coordinator fixtures additionally disable auto-dispatch and substitute planning/classification seams.
  [TestFileEditAgentRunner.cs:44–80](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Helpers/TestFileEditAgentRunner.cs#L44), [CoordinatorWebApplicationFactory.cs:214–253](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Helpers/CoordinatorWebApplicationFactory.cs#L214)

- **Important stronger correction:** public `POST /api/runs` returns 410. The existing `WorkflowIntegrationTests` test accepts **401 or 410**; it does not demonstrate agent → review → merge. Do not use its name as evidence for that traversal.
  [RunEndpoints.cs:41–44](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/RunEndpoints.cs#L41), [WorkflowIntegrationTests.cs:20–31](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/WorkflowIntegrationTests.cs#L20)

- **Real-process ≠ deployed end-to-end.** MCP tests launch a real MCP executable over loopback, but provide a synthetic issuer/JWKS and stub API route. Postgres integration uses a real `postgres:16-alpine` Testcontainer and migrations—not Kubernetes.
  [McpBrokerRealProcessTests.cs:27–112](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Mcp/McpBrokerRealProcessTests.cs#L27), [PostgresFixture.cs:9–29](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/PostgresIntegration/PostgresFixture.cs#L9)

### Events, persistence, lifecycle

- **Postgres delivery is durable append plus shared-table polling**, with a 250-ms idle poll interval and database-side per-run sequence serialization—not local-channel cross-replica fanout. SQLite separately writes through then publishes to a local channel.
  [EfRunEventStream.cs:15–37,143–168](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs#L15), [SqliteRunEventStream.cs:92–116,172–202](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs#L92)

- **Do not promise a terminal event makes the log immutable or guarantees delivery of every future append to an existing subscriber.** EF drops post-completion message deltas, still persists diagnostics/other late events, and subscription termination follows the replay batch. Also, `EfRunEventStreamTests` use SQLite: they are not evidence for PostgreSQL advisory locks.
  [EfRunEventStream.cs:69–86,143–190](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs#L69), [EfRunEventStreamTests.cs:20–28](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/EfRunEventStreamTests.cs#L20)

- **Production is not API-single-writer SQLite.** Provider-selected stores/events/checkpoints are registered explicitly; manifests specify two API replicas, separate workers, Postgres and shared workspace. Children get independent worktrees.
  [Program.cs:1036–1065](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Program.cs#L1036), [api-deployment.yaml:13,337–354](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/k8s/base/api-deployment.yaml#L337), [worker-deployment.yaml:113–150](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/k8s/base/worker-deployment.yaml#L113), [RunOrchestrator.cs:301–312](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Runs/RunOrchestrator.cs#L301)

- **Not every gate is RequestPort, and recovery is conditional.** Collective review uses `TaskCompletionSource`; collective executors are invoked directly. Existing restart tests cover missing worktrees/tree hashes and mismatches failing rather than universally resuming.
  [AssemblyReviewGate.cs:6–43](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/AssemblyReviewGate.cs#L6), [CollectiveAssemblyPipeline.cs:132,499](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs#L132), [WorkflowRestartServiceTests.cs:113,348,397](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/WorkflowRestartServiceTests.cs#L113)

- **Assistant Idle is nonterminal; Completed or durable `run.completed` rejects resume with 409 `operator_run_closed`.** CAS wake is a state-transition guarantee, not a blanket exactly-once-turn-delivery guarantee. Defaults are configurable 5/30 minutes; history is capped at 24 messages.
  [AssistantRunService.cs:32–42,155,900–937](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Assistant/AssistantRunService.cs#L900)

- **Deletion is not a reversible availability state.** It CASes Active → Deleting, sweeps an explicitly listed set of run statuses, releases workspace, then deletes metadata while preserving files. Do not label that list “every possible nonterminal status” without reconciliation.
  [ProjectService.cs:297–343](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Projects/ProjectService.cs#L297)

### Auth ordering, admission, and GitHub capabilities

- **Actual middleware order:** endpoint-integrity → authentication → unmatched-endpoint handling → authorization. Resource authorization remains inside handlers. Do not universally draw resource authorization before every validation/read: blueprint generation validates description, parses/loads project, checks Owner, then begins AI execution.
  [Program.cs:1277–1281](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Program.cs#L1277), [BlueprintEndpoints.cs:100–138](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs#L100)

- **Admission is separate from model preference.** Preparation binds operation/project/subject/provider identity/expiry; acceptance rejects missing, expired, tampered or changed context; pre-call revalidation rejects provider change/unavailability. Blueprint/workflow preference IDs are passed afterward. Durable provenance and credential-drift guards have focused tests.
  [AiExecutionPlanService.cs:256–355,384–414](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs#L256), [BlueprintEndpoints.cs:143–150](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/BlueprintEndpoints.cs#L143), [GenerationModelProviderExecutorTests.cs:83,201–263](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Auth/GenerationModelProviderExecutorTests.cs#L83)

- **Project repository selection is a distinct capability.** Creation consumes/resolves a caller-bound code; regression tests reject direct repository input. This does not grant the suggestion service ambient credentials: current DI supplies a boundary whose access-token method returns null. Suggestions therefore make anonymous reads; caller cancellation is rethrown.
  [ProjectEndpoints.cs:1232–1264](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs#L1232), [GitHubRepositorySelectionEndpointsTests.cs:88](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Auth/GitHubRepositorySelectionEndpointsTests.cs#L88), [Program.cs:265–271](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Program.cs#L265), [EntraOnlyGitHubCredentialBoundary.cs:38–39](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs#L38), [GitHubRepoBlueprintSuggestionService.cs:89–115](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs#L89)

- **MCP requires broker identity and `mcp:invoke`, including exact audience/resource checks.** Assistant remoting requires a per-turn broker token plus renewal callback and fails when cluster pod lifecycle is unavailable. It is not raw Entra/GitHub-token forwarding. SDK session storage is disabled and available tools come from declarations.
  [McpBrokerAuthenticationHandler.cs:64–88](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs#L64), [RemoteOperatorAssistantAgent.cs:77–113](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs#L77), [OperatorAssistantAgent.cs:568–597](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs#L568)

### Best-effort and frontend qualifications

- **Skills:** lookup failure logs and returns no block; shared-workspace success materializes before emitting a pointer; write failure/no shared workspace inlines instructions. Stale-folder deletion is best-effort, not guaranteed removal. Defaults apply recomputes preview and checks digest/store state before applying.
  [SkillPromptComposer.cs:52–126,171–190](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Skills/SkillPromptComposer.cs#L52), [SkillDefaultsService.cs:231–252](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Skills/SkillDefaultsService.cs#L231)

- **Agent template:** five outputs are real, but “never throws” is broader than implementation: only IO, unauthorized-access and security exceptions are caught. Existing-file detection is not an atomic cross-process no-clobber primitive.
  [gen-docs.mjs:263–289](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/scripts/gen-docs.mjs#L263), [AgentDefinitionTemplate.cs:50–69](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Api/Projects/AgentDefinitionTemplate.cs#L50)

- **Frontend corrections are source-supported:** current route map includes the omitted surfaces; API_URL is origin-only; Entra authorize/session exchange and sessionStorage are implemented; `/docs` redirects externally.
  [App.tsx:82–125](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/App.tsx#L82), [config.ts:13–36,128–150,232](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/src/config.ts#L13), [Web Program.cs:52–53](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/Agentweaver.Web/Program.cs#L52)

## 4. Existing focused selectors / commands

**For a later execution phase only; none were run here.**

```powershell
$testProject = 'C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\tests\Agentweaver.Tests\Agentweaver.Tests.csproj'

dotnet test $testProject --filter "FullyQualifiedName~EfRunEventStreamTests"
dotnet test $testProject --filter "FullyQualifiedName~RunEventStreamPostgresTests"
dotnet test $testProject --filter "FullyQualifiedName~OpenIddictAuthorizationServerTests|FullyQualifiedName~McpBrokerRealProcessTests"
dotnet test $testProject --filter "FullyQualifiedName~AiExecutionContextEndpointsTests|FullyQualifiedName~GenerationModelProviderExecutorTests|FullyQualifiedName~GitHubRepositorySelectionEndpointsTests"
dotnet test $testProject --filter "FullyQualifiedName~AssistantRunConcurrencyAndPodLifecycleTests|FullyQualifiedName~WorkflowRestartServiceTests|FullyQualifiedName~SecurityAndRaceTests"
dotnet test $testProject --filter "FullyQualifiedName~SkillPromptInjectionTests|FullyQualifiedName~SkillPodPerRunDeliveryTests|FullyQualifiedName~SkillDefaultsEndpointsTests|FullyQualifiedName~SkillCatalogTests"

npm --prefix 'C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring\apps\web' test
```

Relevant test anchors:

- Cross-stream tail/late events: [EfRunEventStreamTests.cs:28,74,190,208](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/EfRunEventStreamTests.cs#L28).
- PostgreSQL concurrency/idempotence/conflicting payload: [RunEventStreamPostgresTests.cs:15,85,115](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/PostgresIntegration/RunEventStreamPostgresTests.cs#L15). Requires container support.
- OAuth refresh/consent and real MCP forwarding: [OpenIddictAuthorizationServerTests.cs:196,341,491](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Auth/OpenIddictAuthorizationServerTests.cs#L196), [McpBrokerRealProcessTests.cs:235–255](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Mcp/McpBrokerRealProcessTests.cs#L235). The latter launches a process.
- Provider-binding/expiry and pod lifetime: [AiExecutionContextEndpointsTests.cs:413](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Auth/AiExecutionContextEndpointsTests.cs#L413), [AssistantRunConcurrencyAndPodLifecycleTests.cs:108–218](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Assistant/AssistantRunConcurrencyAndPodLifecycleTests.cs#L108).
- Review CAS and stale defaults without partial writes: [SecurityAndRaceTests.cs:156](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/SecurityAndRaceTests.cs#L156), [SkillCatalogTests.cs:500,608](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/tests/Agentweaver.Tests/Skills/SkillCatalogTests.cs#L500).
- Frontend command: [package.json:11](C:/Users/asabbour/Git/agentweaver/.worktrees/drawio-diagram-authoring/apps/web/package.json#L11).

### Explicit unresolved conflicts

1. **28 vs 30 asset ownership**, including the event canonical needed before the overview merge.
2. **Unnamed provider-admission proposal** exists in the audit, but the manifest has no proposed diagram names.
3. **Foreign canonical readiness**—AKS, event/replay, auth and memory trust-boundary reconciliation remains owner work; replacement is conditional.
4. **Overbroad guarantees:** workflow-test traversal, exactly-once conversation execution, universal deletion cancellation, unconditional skill cleanup, and template “never throws” must be narrowed as above.
5. **CI/isolated-shard claims remain unvalidated here**, deliberately: no pipeline investigation. Existing tests and configuration are evidence of available coverage, not successful execution or deployed-cluster proof.
