## Thread 2 — connector and sequence evidence

Read-only research completed inside the specified worktree. Covered **27 retained/redesigned survivors plus `canonical-provider-admission`**. No edits, network calls, runtime actions, or test execution.

**Path abbreviations:**
`A` = `apps\Agentweaver.Api`; `H` = `apps\Agentweaver.AgentHost`; `M` = `apps\Agentweaver.Mcp`; `R` = `packages\Agentweaver.AgentRuntime`; `T` = `packages\Agentweaver.AgentTools`; `W` = `packages\Agentweaver.Squad\Catalog\Resources\workflows`; `K` = `k8s\base`. All references below are relative to the worktree.

### Critical reconciliation findings

- **Collective coordinator PR publication is not verified.** Current collective assembly explicitly executes **merge → Scribe → decision promotion → Complete** (`A\Coordinator\CoordinatorAssemblyService.cs:1679–1783`). The default single-run workflow explicitly executes **merge → `open_pull_request` → Scribe**. Do not transfer that arrow into collective orchestration merely because catalog-test comments describe platform-appended publication.
- **`push-pr` does not itself prove a git push.** Its executor calls GitHub PR creation and can reuse an existing PR; an unpushed/unknown head is a failure condition. Label it **“publish/reuse PR”**, not “push commits,” absent another verified push path.
- **Sandbox repository credentials currently reach execution.** This is constrained direct `git`/`gh` credential delivery, not blanket ambient-shell credentials and not “no repository token.”
- Several current source comments and written-doc diagrams are stale. Implementation statements below take precedence.

## Shared survivors

### 1. `assistant-runtime-fig1`

- **Caller → AssistantRunService:** conversational turn; **service → broker issuer → AgentHost:** API-issued, five-minute MCP credential plus renewal callback, not the caller’s raw Entra bearer (`A\Assistant\AssistantRunService.cs:789–813`; `A\Auth\OAuth\OperatorAssistantBrokerTokenIssuer.cs:30,96–110`).
- **API → held/new AgentHost:** capability-aware launch or held-pod reuse; **API → AgentHost `/configure/mcp-token`:** renewal (`A\Sandbox\KubernetesSandboxExecutor.cs:395–423,511–575`; `A\Assistant\RemoteOperatorAssistantAgent.cs:326–336`).
- **Operator agent → fresh SDK session → MCP tools:** fresh session each turn; credential refreshed before tool invocation (`R\OperatorAssistantAgent.cs:210,459–480,552–564`).
- **AgentHost events → durable stream:** `RemoteOperatorAssistantAgent.cs:316–320`.
- **Idle → rehydrate history → CAS wake → InProgress**; **Completed/run.completed → reject resume** (`AssistantRunService.cs:906–931`). Pod-idle release and conversation parking are separate (`1011–1065,1122–1156`).
- **Omit:** raw Entra propagation; Completed→wake; “held pod means reused SDK session.”

### 2. `auth-security-fig1`

- **Request + endpoint metadata → scheme selector** (`A\Auth\AgentweaverAuthentication.cs:21–59`).
- Exact selection branches:
  - InternalService endpoint → internal-service key.
  - RunCapability endpoint + matching bearer/run-token headers → run capability.
  - Matching internal API key → internal-service scheme.
  - PlatformOrMcp / AuthenticatedSelfOrMcp + broker-looking bearer → BrokerBearer.
  - ProtocolManaged / AuthenticatedSelf + no Authorization header + session cookie → BrowserSession.
  - Otherwise → Entra.
- **Authenticated principal → endpoint policy → resource authorization**, not authentication alone (`A\Auth\EndpointAuthorization.cs:34–50,108–132`; `A\Auth\PlatformRoleAuthorization.cs:34–62`; example run access: `A\Endpoints\RunEndpoints.cs:74,875`).
- **Entra redirect/callback → browser session**; GitHub connection callbacks are separate capability flows (`A\Endpoints\AuthEndpoints.cs:290–453`).
- **Run capability → only constrained endpoint operations:** route/run/header binding; GET plus the named model-validation POST exception (`AgentweaverAuthentication.cs:293–306`).
- **Omit:** GitHub sign-in as product identity; org middleware; universally accepted cookie/broker/run credentials.

### 3. `auth-security-fig4`

- **MCP client → API OpenIddict authorization server:** authorization-code/PKCE flow; reference refresh tokens (`A\Program.cs:935,982–1002`).
- **Canonical public origin → issuer; origin + `/mcp` → exact resource/audience; `mcp:invoke` → required invocation scope** (`A\Auth\OAuth\OAuthServerConfiguration.cs:24,58–59`).
- **Client bearer → MCP validation:** canonical issuer, exactly one expected audience, nonempty `kid`, RS256, required scope (`M\McpBrokerAuthenticationHandler.cs:66–74`).
- **MCP → API:** forwards the **validated same broker token**; API independently validates it and applies endpoint/resource authorization (`M\AgentweaverApiClient.cs:352–379`; `A\Auth\AgentweaverAuthentication.cs:230–240`).
- **MCP protected-resource metadata → authorization server discovery** (`M\Program.cs:92–111`).
- **Omit:** jti-denylist architecture; trusted org claim; MCP replacing the user with an omnipotent API key.

### 4. `canonical-agent-communication-handoff`

- **Goal → drafted outcome → confirmation/revision gate:** `A\Coordinator\CoordinatorRunService.cs:669–693`.
- **Confirmed outcome → workflow selection/decomposition → persisted work plan/subtasks/dependency edges** (`A\Coordinator\CoordinatorOrchestratorExecutor.cs:124–180,1842–1911`).
- **Dependency frontier → child runs → observed results → next frontier** (`A\Coordinator\CoordinatorDispatchService.cs:328–543,966–994`).
- Child carries **ParentRunId/SubtaskId**; satisfied dependencies mean `assemble_ready`/`completed`, not arbitrary peer delivery (`CoordinatorDispatchService.cs:50–56,875–888`).
- **Failed/RAI-flagged child → blocked dependents** (`989–994`).
- **Omit:** all-to-all peer arrows; dependency-free dispatch; Decisions→Memory chain as the subject of this diagram.

### 5. `canonical-aks-components`

- **Application Gateway routes → frontend/API/MCP Services** (`K\httproute-frontend.yaml:23–34`; `K\httproute-api.yaml:20–63`; `K\mcp-httproute.yaml:9–46`).
- **MCP → API HTTP:** `K\mcp-deployment.yaml:48–49`.
- **API + Worker → Postgres:** both deployments contain database connections (`K\api-deployment.yaml:338–348`; `K\worker-deployment.yaml:149–159`).
- **Heartbeat → shared Ready records → atomic pickup/reservation**, not an invented external queue (`A\Coordinator\CoordinatorHeartbeatService.cs:110–121`; `CoordinatorPickupService.cs:187–197`).
- **API/Worker → SandboxClaim → bound AgentHost → authenticated A2A** (`A\Sandbox\KubernetesSandboxExecutor.cs:434–452,629–712`).
- **API/Worker → CSI application secrets:** `K\api-deployment.yaml:360,419`; `K\worker-deployment.yaml:217,270`.
- **Preview Gateway → preview Service → sandbox port** is a separate ingress plane.
- Scale labels: API **2**, Worker **2; HPA 2–3**, MCP **1** (`K\api-deployment.yaml:13`; `worker-deployment.yaml:13`; `worker-hpa.yaml:65–66`; `mcp-deployment.yaml:10`).
- **Omit:** Worker-only database access; MCP CSI mounts (`mcp-deployment.yaml:55,83` are empty); AgentHost→vault credential fetch (`scripts\azure\steps\15-setup-identity.mjs:145–150`).

### 6. `canonical-board-lifecycle`

- **Backlog → Ready → claimed coordinator run**, with atomic pickup and no silent requeue after activation failure (`A\Coordinator\CoordinatorPickupService.cs:49–52,187–197,261`).
- **Ready + unresolved dependency → remains Ready, blocked flag**, not another board column (`A\Backlog\BacklogTaskReadModelFactory.cs:46–52`).
- **Persisted run/work-plan state → projected board bucket**, rather than board columns driving execution (`A\Runs\BoardProjectionService.cs:77–122`).
- Mapping:
  - Failed/Declined/MergeFailed → Problems.
  - Completed/Merged/AssembleReady → Done.
  - AwaitingReview → Human Review.
  - AssemblyBlocked/AssemblyFailed/AssemblyDeclined → Problems.
  - WorkPlan Complete → Done; InReview → Human Review; otherwise assembly stage or Active.
  - Evidence: `A\Runs\WorkflowStageProjector.cs:55–79`.
- **Omit:** approval→Done; all terminal statuses→Done.

### 7. `canonical-coordinator-journey`

- **Goal → confirmed outcome → dependency plan → children → collective assembly**, as #4.
- **Delivered branches → integration branch → selected assembly gates → human review** (`A\Coordinator\CoordinatorAssemblyService.cs:917–991,1023–1024,1110–1133,1165`; `CollectiveAssemblyPipeline.cs:410–415`).
- **Approval → merge**; **actual merge conflict → resolution/recovery**; **other failed merge → MergeFailed + Scribe** (`CoordinatorAssemblyService.cs:1679–1741`).
- **Successful merge → Scribe → promote decisions → recorded Complete** (`1753–1783`).
- **Changes requested → steering/revision handling**, not automatic unconditional replay (`1410–1414,2220–2227`).
- **Blocked → eligibility recovery or resumed dispatch; timeout → failure** (`4481–4515`).
- **Omit:** assembly→Done shortcut; blocked=declined; guaranteed collective PR-publication step.

### 8. `canonical-default-workflow`

Exact declared edges in `A\Workflows\DefaultWorkflowTemplate.cs:120–160`:

- Agent → RAI.
- RAI → Agent **revise**; → safety-failed terminal **safety-failed**; → Scribe **no-changes**; → Review **review**.
- Review → Merge **approved**; → Agent **request-changes**; → declined terminal **declined**.
- Merge → publish/reuse PR **merged**; → Review **blocked**.
- PR action → Scribe → Done.

Important guards:

- The `merged` branch includes successful or terminally failed merge handling in binder wiring (`A\Workflows\RunWorkflowGraphBinder.cs:398–420`).
- PR action requires head branch and live repository capability; disconnected repository **skips**; failures still pass output to Scribe (`R\Workflow\OpenPullRequestTurnExecutor.cs:95–165`).
- Existing PR can be reused (`A\Github\GitHubPullRequestClient.cs:74–77`).
- **Omit:** merge→Scribe bypass on the publication path; PR success required for Scribe; “PR action pushes commits.”

### 9. `canonical-durable-event-stream`

- **Producer → RunStreamEntry → shared IRunEventStream/RunEvents → local notification**; durable stream allocates authoritative sequence (`A\Infrastructure\RunStreamStore.cs:183–212`).
- **Replica A/B subscriber → shared RunEvents query → ordered events → SSE client** (`A\Infrastructure\EfRunEventStream.cs:144–167,409–427`).
- **Postgres writer → per-run advisory transaction lock → MAX+1 → commit** (`EfRunEventStream.cs:226–282,316–321`).
- **Same-replica RunStreamStore** remains a compatibility/low-latency path; it is not cross-replica fan-out.
- **Omit:** shared live channel/broker as the Postgres delivery mechanism; pod-generated sequence as globally authoritative.

### 10. `canonical-durable-event-stream-sequence`

Primary EF/Postgres sequence:

1. Producer → durable append/commit → assigned sequence.
2. Client → SSE endpoint with Last-Event-ID.
3. Endpoint → subscriber with cursor.
4. Subscriber → DB `Sequence > cursor`, ordered ascending.
5. Each yielded event → advance cursor.
6. Empty batch → wait **250 ms** → query again.
7. Drain entire loaded batch; terminal event in batch → close.

Evidence: `A\Infrastructure\EfRunEventStream.cs:37,144–183,409–427`; `A\Endpoints\RunEndpoints.cs:479–491`; SSE frame `A\Endpoints\EndpointHelpers.cs:466`.

Separate SQLite lane only:

- **Subscribe/register local channel → durable replay → local channel tail → discard already-replayed sequences** (`A\Infrastructure\SqliteRunEventStream.cs:172–208`).
- **Append → SQLite write → bounded local channel publication** (`79–115`).

Retryable `assembly_blocked` is **not terminal**; `assembly_failed` is. Tests: `tests\Agentweaver.Tests\EfRunEventStreamTests.cs:74–185`.

### 11. `canonical-memory-context`

Independent incoming arrows:

- **Approved active architectural/scope decisions → compiler** (`A\Memory\MemoryContextCompiler.cs:58–64`).
- **Target-agent nonlegacy core memories → candidate selection** (`67–74`).
- **High-importance own nonlegacy learning OR approved cross-team-tagged learning → candidate selection** (`76–88`).
- **Latest open project session → compiler** (`92–97`).
- **Combined memory candidates → importance then recency sort → bounded memory selection → compiler** (`105–139`).
- **Compiler → explicitly untrusted JSON context**, not executable instructions (`174–214`).
- **Omit:** decisions→core→learning→cross-team serial pipeline; strict core-first ordering; claiming memory-selection bounds cap the entire decision/session payload.

### 12. `canonical-sandbox-boundary`

- **Model tool call → governance kernel AND direct sandbox policy → allow/deny** (`R\SandboxGovernance.cs:114–133`; deny-default requirement `85–88`).
- **Allowed shell request → approval checks → shell/path validation → SandboxCommand → executor** (`T\Tools\RunCommandTool.cs:19–82,86–131,148`).
- **AgentHost → pod-private executor client/Unix socket → executor sidecar process** (`H\Program.cs:79–108`; `K\sandbox-template-agenthost.yaml:158,277–280`).
- **Executor → permitted workspace/scratch roots**; shared and pod-local workspace modes must not be conflated (`RunCommandTool.cs:86–103`; `H\PodLocalWorkspaceManager.cs:46–155`).
- **Pod event → A2A DataContent → RemoteAgentProxy → recording writer → run stream** (`R\Workflow\RemoteAgentProxy.cs:250–251,321–342`).
- **API broker → AgentHost runtime repository credential → constrained direct git/gh execution** (`A\Sandbox\KubernetesSandboxExecutor.cs:697–702`; `H\RunScopedRepositoryCredentialProvider.cs:9`; `R\CopilotAIAgent.cs:452`; `T\Tools\RunCommandTool.cs:238–271`).
- **Omit:** blanket “no repository tokens”; direct model→unrestricted host shell; AgentHost→Key Vault fetch.

### 13. `canonical-sandbox-experience`

Two related, nonidentical flows:

- **Child run → isolated worktree/branch → delivered result → integration branch → review/merge** (`A\Runs\RunOrchestrator.cs:287–322`; `A\Coordinator\CollectiveAssemblyPipeline.cs:410–458`).
- **Run → sandbox claim/pod binding → durable `sandbox.execution_pod.bound` → operator observation** (`A\Sandbox\KubernetesSandboxExecutor.cs:629–632`; `RunEventExecutionPodNameStore.cs:16,39–54`).
- Pod-local mode additionally has **verified source commit/tree → local workspace → prepared writeback result/ref** (`H\PodLocalWorkspaceManager.cs:111–155,218–375`).
- **Omit:** pod identity = worktree identity; every run always writes directly to shared RWX storage; observation endpoint list as the assembly model.

### 14. `canonical-workflow-authoring`

- **Natural-language request → generator → draft YAML → loader → binder dry-run** (`A\Workflows\CopilotWorkflowGenerator.cs:54–112`).
- **Invalid draft → one correction call → loader/binder again**; second failure → error, no persisted draft (`71–83`).
- **Valid draft → human review/edit → explicit save endpoint → loader/binder validation → contained workspace write → registry Sync → reloaded definition** (`A\Workflows\WorkflowDefinitionEndpoints.cs:441–504,518–524,547–549`).
- **Omit:** generation automatically saves; schema-only validation; unlimited correction loop.

### 15. `canonical-workflow-selection`

- **Registry available valid definitions → trigger-agnostic candidates** (`A\Coordinator\CoordinatorOrchestratorExecutor.cs:287–298`).
- **Explicit request/backlog override → selected workflow**, if found (`299–325`).
- Otherwise **conversational “use …” override → selected workflow**, if found (`331–347`).
- Otherwise **zero/singleton → available/default result** (`360–372`).
- Otherwise **model selection → parse/normalized ID-or-name matching → selection**; parse/unknown choice retries at most twice (`A\Coordinator\WorkflowSelector.cs:94–176,224–234`).
- **Model exception → immediate fallback**; exhausted parse attempts → fallback.
- Fallback: **default/standard → first non-code-review → first entry only as last resort** (`195–215`).
- **Selected workflow + decomposition → compatibility revalidation** (`CoordinatorOrchestratorExecutor.cs:180,407–488`).
- **Omit:** ResolveInvocationKindAsync; configured default always wins; universally first-entry fallback.

### 16. `email-architecture`

- **Browser → authenticated API** (`apps\web\src\api\client.ts:304,1589–1593`).
- **External MCP client → MCP validation → API with same validated bearer**, as #3.
- **API → orchestration/runtime → per-run AgentHost execution** (`A\Program.cs:166–228`; `A\Sandbox\KubernetesSandboxExecutor.cs:434–452,691–712`).
- **API/Worker → Postgres and workspace** (`K\api-deployment.yaml:338–360`; `worker-deployment.yaml:149–159,211–217`).
- **API credential broker → repository/Copilot capability → runtime** (`A\Auth\GitHubCapabilityBroker.cs:35–117`).
- **Execution → GitHub repository operations** is currently supported through constrained credential-bearing commands (#12).
- **Execution events → shared log → browser/MCP watch**, as #9–10.
- **Omit:** repository access removed merely to satisfy the desired contract; GitHub identity replacing Entra.

### 17. `email-components`

Use dependency arrows, not deployment/network arrows:

- **API → Api.Data, Domain, SandboxFs, AgentRuntime, Squad, Postgres migrations** (`A\Agentweaver.Api.csproj:4–11`).
- **API → OpenIddict server/validation + EF providers + workflow framework** (`A\Agentweaver.Api.csproj:26–39`).
- **AgentHost → AgentRuntime + Domain** (`H\Agentweaver.AgentHost.csproj:4–5`).
- **MCP → ModelContextProtocol + OpenIddict validation** (`M\Agentweaver.Mcp.csproj:10–12`).
- **MCP API client → API HTTP boundary**, not a project-reference dependency (`M\AgentweaverApiClient.cs:376–380`).
- **API workflow definition → classifier/executor binder → runtime executor wiring** (`A\Workflows\RunWorkflowGraphBinder.cs:335–425`; `docs\workflow-binder.md:17–20`).
- **Omit:** direct MCP→database/library ownership unless separately evidenced; confusing project references with runtime data movement.

### 18. `email-coordinator-workflow`

Sequence version of #4 and #7:

- Caller → outcome confirmation → persisted DAG → dependency-ready children.
- Child results → assembly eligibility → integrated branch/gates → human review.
- Approval → merge; changes → steering/revision; decline → declined terminal (`A\Coordinator\CoordinatorAssemblyService.cs:1405–1439`).
- Retryable blocked → wait/recovery/redispatch; timeout → failure (`4481–4515`).
- Merge success/failure → recorded Scribe outcome (`1735–1758`).
- Final-Scribe admission/recovery also exists for top-level coordinators (`504–526,1879–1954`).
- **Omit:** declined-or-blocked as one terminal branch; Scribe only after successful merge.
- **Flag, do not assert:** collective merge→PR-publication edge, which remains unsupported by the traced implementation.

### 19. `sandbox-browser-preview-fig1`

- **API → Kubernetes:** labels pod; creates ClusterIP Service and HTTPRoute (`A\Sandbox\Preview\SandboxPreviewService.cs:218–281`).
- **Service port 80 → selected sandbox target port** (`237–269`).
- **HTTPRoute → shared preview Gateway** (`271–281`).
- **API → exact generated HTTPS preview URL:** mandatory publication probe before successful return; same-origin redirect restriction (`305–329,332–440`, specifically `392–393`).
- **Browser → preview Gateway → Service → sandbox process**.
- **Preview Gateway only → sandbox ports 3000–9000** (`K\networkpolicy-sandbox.yaml:51–60`).
- **Probe failure → route/service cleanup** (`SandboxPreviewService.cs:308–319`); **expiry → reaper deletion** (`881–923`).
- **Omit:** API “creates objects only”; direct API→pod preview-port probe (`207–211` explicitly rejects that path).

## Seven authored workflow diagrams

These arrows describe **the authored YAML graph**, not an automatically proven collective merge/PR/Scribe extension. `tests\Agentweaver.Tests\Workflows\CatalogWorkflowBindingTests.cs:20–51` verifies load/bindability and gate inventory; its publication comment is not proof of collective PR execution.

### 20. `workflow-agent-evaluation`

`W\agent_evaluation.yaml:63–90`:

- Setup → Run → Collect → Safety.
- Safety → Setup **revise**; → safety-failed **safety-failed**; → Done **no-changes**; → Report **review**.
- Report → Done.
- Setup/run/collect/report are **prompt steps** (`14–47`).
- **Omit:** fan_out/fan_in parallel execution; universal human-review gate.

### 21. `workflow-bug-fix`

`W\bug_fix.yaml:84–144`:

- Triage → Fix → Verify.
- Verify → RAI **approved**; → Fix **request-changes**; → declined **declined**.
- RAI → Fix **revise**; → safety-failed **safety-failed**; → Done **no-changes**; → Build/Test **review**.
- Build/Test → Human Review **approved**; → Fix **request-changes**; → declined **declined**.
- Human Review → Done **approved**; → Fix **request-changes**; → declined **declined**.
- Verify is `peer_review`; Build/Test is `build_test` (`27–50`).
- **Omit:** missing RAI/build gate; authored merge/Scribe arrows.

### 22. `workflow-content-authoring`

`W\content_authoring.yaml:82–122`:

- Research → Draft → Edit → RAI.
- RAI → Draft **revise**; → safety-failed **safety-failed**; → Done **no-changes**; → Human Review **review**.
- Human Review → Publish **approved**; → Draft **request-changes**; → declined **declined**.
- Publish → Done.
- Edit and Publish are **prompt** steps (`26–29,56–59`).
- **Omit:** Edit as a verdict gate; Publish=git merge/platform PR publication.

### 23. `workflow-incident-response`

`W\incident_response.yaml:63–87`:

- Triage → Mitigate → Verify → Review Gate.
- Review Gate → Postmortem **approved**; → Mitigate **request-changes**; → declined **declined**.
- Postmortem → Done.
- Verify is a **prompt**, review-gate is a **human-review check** (`26–38`).
- **Omit:** authored Scribe; invented RAI gate; automatic remediation approval.

### 24. `workflow-infra-ops`

`W\infra_ops.yaml:92–148`:

- Plan → Implement → Validate.
- Validate → RAI **pass**; → Implement **fail**.
- RAI → Implement **revise**; → safety-failed **safety-failed**; → Done **no-changes**; → Infra Review **review**.
- Infra Review → Human Review **approved**; → Implement **request-changes**; → declined **declined**.
- Human Review → Done **approved**; → Implement **request-changes**; → declined **declined**.
- Validate and Infra Review are `peer_review` (`34–57`).
- **Omit:** validation “approved/declined” edges—the YAML uses pass/fail; an invented build_test node.

### 25. `workflow-pm-discovery`

`W\pm_discovery.yaml:56–77`:

- Research → Synthesis → Review → Review Gate.
- Review Gate → Done **approved**; → Synthesis **request-changes**; → declined **declined**.
- Review is **prompt**; gate is **human-review check** (`25–37`).
- **Omit:** Review as peer-review executor; authored merge or Scribe.

### 26. `workflow-software-delivery`

`W\software_delivery.yaml:100–168`:

- Plan → Implement → Test Gate.
- Test Gate → RAI **pass**; → Implement **fail**.
- RAI → Implement **revise**; → safety-failed **safety-failed**; → Done **no-changes**; → Rubberduck **review**.
- Rubberduck → Code Review **pass**; → Implement **revise**.
- Code Review → Build/Test.
- Build/Test → Review Gate **approved**; → Implement **request-changes**; → declined **declined**.
- Review Gate → Done **approved**; → Implement **request-changes**; → declined **declined**.
- Code Review is **prompt**, not verdict-routed peer review (`56–59`).
- **Omit:** collapsed review gates; omitted rubberduck/build-test; universal serial happy-path-only depiction.

## Proposed canonical

### 27. `canonical-provider-admission`

**Prepare → accept → durable provenance → launch/invocation fences**, with separate model-provider and GitHub-capability snapshots:

1. **Caller + operation + scope → execution-context endpoint → resolver → prepared context/key.** Endpoint authorizes requested scope (`A\Endpoints\AiExecutionContextEndpoints.cs:122–199`).
2. **Resolver → selected provider or unavailable.** Project/platform precedence differs from personal-session precedence (`A\Auth\EffectiveModelProviderResolver.cs:41–126`). Do not draw one universal fallback chain.
3. **Prepared context → signed key:** binds version, operation, project, subject, provider-identity digest, expiry (`A\Auth\AiExecutionPlanService.cs:256–290`).
4. **Mutation request + `If-Model-Provider-Key` → AcceptAsync:** verify signature/expiry/bindings, re-resolve current provider, compare identity; missing/expired/changed → replacement context and rejection (`A\Endpoints\EndpointHelpers.cs:50–63`; `AiExecutionPlanService.cs:293–358`).
5. **Accepted plan → pre-invocation revalidation** (`AiExecutionPlanService.cs:384–413`).
6. **Run → durable model-provider snapshot ownership:** database owner points to private secret containing provider/BYOK configuration; concurrent losers reuse winner and clean their candidate (`A\Auth\RunModelProviderSnapshotStore.cs:55–79,162–213`).
7. **Retry/child → inherited durable model boundary**, with ModelSource consistency check (`A\Runs\RunOrchestrator.cs:795–819`).
8. **Root run → live GitHub source capture**; **child/retry → purpose-preserving inherited capability snapshots** (`A\Auth\RunGitHubCapabilitySnapshotLifecycle.cs:40–106`).
9. Provenance fields retained: **purpose, app/source kind, project/subject, source authorization/binding, repository/installation, credential reference/version, grant digest, captured-at, expiry**. Child gets new SnapshotRef/RunId, preserves source tuple (`A\Auth\GitHubConnectionsPersistenceStore.cs:2209–2227`).
10. **Snapshot → live fence:** purpose/ref/expiry plus exact active source/version/digest/revocation checks (`GitHubConnectionsPersistenceStore.cs:1577–1639`).
11. **Fenced repository snapshot → installation token mint → fence again → credential callback**; **fenced Copilot snapshot → freshness/vault read → fence again → callback** (`A\Auth\GitHubCapabilityBroker.cs:35–117`).
12. **Persisted run → invocation guard → durable boundary/expected-provider check → capability readiness → provenance event → model invocation** (`A\Auth\RunModelInvocationGuard.cs:11–59`).

**Branches/omissions:** BYOK does not require unattended Copilot capability; repository capability remains separate. Repository-less intentionally blank origins are supported (`GitHubConnectionsPersistenceStore.cs:2197–2199,2343–2347`). Do not equate prepare with permission to execute, snapshots with irrevocable grants, or fenced capability references with raw credentials.

## Coverage-count correction

The supplied survivor list contains **26 named existing survivors**, not 27: 19 shared diagrams above plus seven authored workflows. With the proposed provider-admission diagram, this report covers **27 diagrams total**. All retained/redesigned entries selected from the shared plan were included.
