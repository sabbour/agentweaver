## Scope and evidence convention

Read-only research completed entirely in the specified worktree. Covered **26 retain/redesign survivors plus `canonical-provider-admission`**. No writes, runtime calls, tests executed, or nested agents.

Paths below are relative to that worktree. Abbreviations:

- **API** = `apps\Agentweaver.Api`
- **Host** = `apps\Agentweaver.AgentHost`
- **Runtime** = `packages\Agentweaver.AgentRuntime`
- **Catalog** = `packages\Agentweaver.Squad\Catalog\Resources\workflows`
- **Tests** = `tests\Agentweaver.Tests`

The selected audit entries contain completed assessments but still have `status: "queued"`—not completed downstream artifacts. “Retain” below means supported semantics, **not visual approval**.

## Important conflicts requiring explicit reconciliation

1. **GitHub is not product authentication.** Current authentication is endpoint-metadata-directed Entra, browser session, broker bearer, internal service key, or run capability. GitHub Repo/Copilot Apps provide separate capabilities.
   Evidence: `API\Auth\AgentweaverAuthentication.cs:22–59`; `API\Auth\EndpointAuthorization.cs:7–30`; `Tests\Auth\LegacyOAuthRetirementTests.cs:17`; `docs\guide\authentication.md:7–15`. `CONTRIBUTING.md:34–36` still advertises obsolete GitHub sign-in configuration.

2. **The assistant no longer propagates raw Entra bearer tokens to MCP.** The API issues/renews short-lived MCP broker tokens. Idle is resumable; Completed is sealed.
   Evidence: `API\Assistant\AssistantRunService.cs:789–813,906–931`; `Tests\Assistant\RemoteOperatorAssistantAgentTests.cs:32`; contrast `docs\deep-dive\assistant-runtime.md:38–48`.

3. **“Credential-free sandbox” is not an accurate implementation description.** API `/configure` delivers a Copilot capability credential and a minted repository access token; AgentHost keeps the repository token in memory; runtime shell tooling supports restricted direct Git/GH credential injection. This is different from ordinary shell processes having unrestricted credentials.
   Evidence: `API\Sandbox\KubernetesSandboxExecutor.cs:697–708,1240–1285`; `Host\RunScopedRepositoryCredentialProvider.cs:5–9`; `Runtime\CopilotAIAgent.cs:452`; `packages\Agentweaver.AgentTools\Tools\RunCommandTool.cs:22–34,231–269`.
   **Normative conflict:** `docs\design\two-github-app-production-contract.md:169–173` requires operation-specific results rather than sandbox token delivery/direct GitHub paths.

4. **Do not infer child behavior from old comments/helpers.** Current child launch creates a **per-child worktree**, and its documented trimmed pipeline has **no per-child RAI/review/merge/Scribe**. A shared-worktree helper remains elsewhere but is not this launch path.
   Evidence: `API\Runs\RunOrchestrator.cs:280–307`; `Tests\Graph\RunWorkflowDefinitionBindingTests.cs:108`; contrast `docs\deep-dive\agent-communication.md:154–160`.

5. **Coordinator PR publication is not verified.** The default standalone template definitely includes `push-pr`. However, inspected collective assembly calls `MergeWorktree`, then Scribe and completion; I found no PR-publication component in that path. Do not copy the audit/test-comment claim of universal platform-appended PR publication into coordinator diagrams without further proof.
   Evidence: `API\Coordinator\CollectiveAssemblyPipeline.cs:439–468`; `API\Coordinator\CoordinatorAssemblyService.cs:1747–1770`; compare `API\Workflows\DefaultWorkflowTemplate.cs:85–94,151–164`.

6. **MCP documentation slightly understates current endpoint acceptance.** Code also permits broker selection on `AuthenticatedSelfOrMcp`, used by execution-context preparation—not only `PlatformOrMcp`.
   Evidence: `API\Auth\AgentweaverAuthentication.cs:47–51`; `API\Endpoints\AiExecutionContextEndpoints.cs:16–36`; contrast `docs\mcp-oauth.md:117–130`.

## Per-diagram implementation/component maps

### Authentication, assistant, provider admission

**`assistant-runtime-fig1` — redesign**

- Components/owners: Sessions client; API assistant endpoints and durable conversation service; broker-token issuer; AgentHost lifecycle/A2A proxy; pod-side operator runner; fresh SDK session with MCP-only tools; API run/event persistence.
- Evidence: `API\Assistant\AssistantRunService.cs:375–389,789–813,885–931`; `API\Assistant\RemoteOperatorAssistantAgent.cs:102–125,326–336`; `Runtime\OperatorAssistantAgent.cs:568–592`.
- Surviving facts: API-owned durable history, held-pod reuse, fresh SDK sessions, MCP tool access. Replace caller-bearer and Completed-resumption claims.
- Symbols: **C4 containers/components**, Kubernetes pod, database cylinder; custom symbols only for product Session/Run identities.

**`auth-security-fig1` — redesign**

- Components/owners: Entra identity provider; browser-session service; endpoint classification/integrity middleware; authentication handlers; platform-role/project-role authorization; separate internal/run capabilities; GitHub capability brokers outside identity.
- Evidence: `API\Auth\EndpointAuthorization.cs:7–30,140–156`; `API\Auth\AgentweaverAuthentication.cs:22–59`; `API\Program.cs:341–370`.
- Preserve conceptual authentication/authorization boundary, not GitHub token validation/org membership middleware.
- Symbols: **Azure Entra**, C4 security components and trust-boundary containers; flowchart decision for endpoint classification.

**`auth-security-fig4` — redesign**

- Components/owners: external MCP client; API-hosted OpenIddict authorization server; upstream Entra/browser consent; durable OAuth records and Key Vault certificates; MCP resource server; API resource authorization.
- Evidence: `docs\mcp-oauth.md:3–22,70–89`; `apps\Agentweaver.Mcp\Program.cs:63–68,100`; `apps\Agentweaver.Mcp\McpBrokerAuthenticationHandler.cs:66–80`; `API\Auth\AgentweaverAuthentication.cs:230–250`.
- Survives: broker-mediated MCP trust and forwarding. Replace bespoke jti/org claims with keyed RS256, canonical origin/exact resource, `mcp:invoke`, endpoint-specific API authority.
- Symbols: **UML sequence/C4 trust boundaries**, Azure Entra/Key Vault, database; not a generic “GitHub login” icon.

**`canonical-provider-admission` — proposed**

- Components/owners: execution-context endpoint → operation catalog and authorization → effective-provider resolver → signed prepared context → guarded acceptance → queued/run snapshot and invocation guard → provider-specific runtime.
- Evidence: `API\Endpoints\AiExecutionContextEndpoints.cs:16–54,119–128`; `API\Auth\AiExecutionPlanService.cs:33–94,256–358`; `API\Coordinator\CoordinatorPickupService.cs:68–100`; `API\Auth\RunModelInvocationGuard.cs:12–43`.
- Separate three scopes:
  - Project: active project Copilot binding wins, unusable active binding fails closed; otherwise platform BYOK then platform Copilot.
  - Platform assistant: platform resolution.
  - Personal session: platform BYOK, personal BYOK, personal Copilot; no platform-Copilot fallback.
- Evidence: `API\Auth\EffectiveModelProviderResolver.cs:41–125`; operation distinctions at `API\Auth\AiExecutionPlanService.cs:83–94`; snapshot ownership tests at `Tests\Auth\RunModelProviderSnapshotStoreTests.cs:16–49`.
- Show operation-specific BYOK support, not universal interchangeability. Symbols: **flowchart/C4**, signed document, database, provider cloud; custom execution-plan/snapshot labels.

### Deployment, sandbox, preview

**`canonical-aks-components` — redesign**

- Components/owners: application Gateway/HTTPRoutes; frontend/API/MCP Services and Deployments; separate worker Deployment; PostgreSQL; RWX workspace; Key Vault/CSI; ACR; AgentHost SandboxTemplate/WarmPool/Claim; separate preview Gateway.
- Evidence: `k8s\base\api-deployment.yaml:13,338–345,406–419`; `worker-deployment.yaml:13,136–156`; `mcp-deployment.yaml:10,83`; `sandbox-template-agenthost.yaml:37–39,72,95,277–280`; `sandbox-warmpool-agenthost.yaml:22–32`.
- Both API and worker use PostgreSQL. Worker HPA is **2–3 replicas, CPU-based**, not queue-based KEDA (`worker-hpa.yaml:49–75`). MCP mounts no secret volumes. AgentHost has a distinct no-Key-Vault-role identity (`serviceaccount-agenthost.yaml:12–20`).
- Symbols: **native Azure and Kubernetes** throughout; custom logical labels only inside workload containers. No mesh-wide Istio boundary.

**`canonical-sandbox-boundary` — redesign**

- Boundaries: model/tool governance; tool-specific validation; workspace/path confinement; executor routing; trusted AgentHost; Unix-socket exec sidecar; process isolation inside Kata VM; durable event/API boundary.
- Evidence: `Runtime\Agentweaver.AgentRuntime.csproj:4–16`; `API\Program.cs:554–586,703–714`; `k8s\base\sandbox-template-agenthost.yaml:270–306`; credential evidence above.
- Sidecar is a distinct container: identity-token masking, separate PID namespace, unprivileged security context. Optional BuildKit is **not base deployment** (`sandbox-template-agenthost.yaml:299–306`).
- Symbols: **nested Kubernetes pod/container and C4 trust boundaries**, filesystem/process symbols. Do not label the whole pod credential-free.

**`canonical-sandbox-experience` — redesign**

- Components/owners: project checkout; isolated child worktrees/branches; coordinator integration/review result; separately observed execution pods; detached Build/Test workspace and preview.
- Evidence: `API\Runs\RunOrchestrator.cs:280–307`; `API\Coordinator\CollectiveAssemblyPipeline.cs:181–200,408–418`; `docs\guide\runs.md:367`; `docs\experience\sandbox-pod-execution.md:21`.
- Preserve “isolated changes then assembled result,” with pod telemetry as an observation layer—not a substitute for worktree ownership.
- Symbols: **flowchart/Git branch and filesystem**, Kubernetes pods; product Run and topology nodes custom. Exact current pod-pill UI rendering was not inspected.

**`sandbox-browser-preview-fig1` — redesign**

- Components/owners: API control plane resolves bound claim, creates selector/Service/HTTPRoute and probes the exact public URL; preview Gateway serves browser data directly; AgentHost PreviewRunner and pod-local TcpPortForwarder expose the app.
- Evidence: `API\Sandbox\Preview\SandboxPreviewService.cs:188–280,333–429`; `Host\PreviewRunner.cs:433–468`; `docs\deep-dive\sandbox-browser-preview.md:30–94`.
- Preserve Gateway → Service → pod/forwarder → app. Add publication validation; do not draw API as preview data proxy or direct API-to-pod preview preflight.
- Symbols: **Kubernetes Gateway/HTTPRoute/Service/pod**, UML control/data lanes, browser/cloud. DNS is managed externally; API does not mutate DNS.

### Coordination and workflows

**`canonical-agent-communication-handoff` — retain**

- Components/owners: human goal; confirmed OutcomeSpec; coordinator-owned WorkPlan/dependency DAG; child runs; parent-owned result assembly.
- Evidence: `API\Coordinator\CoordinatorWorkflowFactory.cs:106–167`; `CoordinatorDispatchService.cs:384–427,508–543`; `API\Runs\RunOrchestrator.cs:280–307`.
- **Truly survives:** bounded subtask ownership, dependency-aware readiness, results returning to coordinator—not sibling chat. A2A remains execution transport, not team messaging (`docs\deep-dive\agent-communication.md:17–22`).
- Symbols: **UML activity/DAG**, C4 coordinator/worker; custom OutcomeSpec/WorkPlan documents. No chat-bus symbol.

**`canonical-board-lifecycle` — redesign**

- Components/owners: backlog store owns intake tasks; heartbeat/pickup claims eligible Ready tasks; board projection combines tasks and top-level runs; stage projector maps persisted run/assembly state.
- Evidence: `API\Runs\BoardProjectionService.cs:65–115`; `WorkflowStageProjector.cs:27–79`; `API\Backlog\BacklogTaskReadModelFactory.cs:24–52`; `CoordinatorHeartbeatService.cs:110–113`.
- Fixed default buckets coexist with explicitly declared workflow stages. Dependency-blocked Ready is metadata, not another column. Approval alone does not mean Done.
- Symbols: **UML state/flowchart**, native document/database for backing entities; product board cards custom.

**`canonical-coordinator-journey` — redesign**

- Components/owners: project/board entry; draft and confirmation gate; selector/planner; dispatch frontier; child workspaces; collective review/build/preview; human decision; merge/recovery; final recording.
- Evidence: `CoordinatorWorkflowFactory.cs:106–167`; `CoordinatorDispatchService.cs:384–543`; `CoordinatorAssemblyService.cs:757–767,1246,1679–1770`.
- Keep this user-oriented; do not reproduce deployment containers. Distinguish blocked/recoverable from terminal failure/decline. Coordinator PR publication remains unverified as flagged above.
- Symbols: **UML activity/flowchart**, human actor, document artifacts; custom product screens.

**`canonical-default-workflow` — redesign**

- Canonical owner: code-embedded `DefaultWorkflowTemplate`, materialized as a project file but retained as fallback.
- Components: Agent, RAI, human Review, Merge, **Push PR**, Scribe, safety/decline/Done terminals.
- Evidence: `API\Workflows\DefaultWorkflowTemplate.cs:40–164`; regression inventory `Tests\Graph\RunWorkflowDefinitionBindingTests.cs:49–64`.
- Preserve revision, no-change, decline, blocked-merge loops. Put Scribe after publication on the ordinary successful path, not universally before review.
- Symbols: **flowchart processes/diamonds/terminals**; product agent-role glyphs optional, not cloud icons.

**`canonical-workflow-authoring` — redesign**

- Components/owners: UI/MCP description; provider-gated generator; loader plus binder dry-run; one correction attempt; editable draft; explicit save; constrained project filesystem; registry refresh.
- Evidence: `API\Workflows\CopilotWorkflowGenerator.cs:54–112`; `WorkflowDefinitionEndpoints.cs:438–585,588–688`; `WorkflowRegistry.cs:57–82`.
- Generation does not itself persist. Save validates separately and updates allowed-set membership. Reserved catalog IDs are protected despite older registry comments (`WorkflowRegistry.cs:182–194`).
- Symbols: **flowchart**, document/YAML and filesystem; C4 generator/validator components.

**`canonical-workflow-selection` — redesign**

- Owners: registry supplies validated available definitions; orchestrator handles explicit/task/conversational overrides; selector chooses automatically; run store records rationale.
- Evidence: `CoordinatorOrchestratorExecutor.cs:271–370`; `WorkflowSelector.cs:95–174,201–218`.
- Candidates are trigger-agnostic. Up to two model attempts; normalized ID/name and unique textual match are supported. Selector fallback prefers default/standard/non-code-review—not universally first entry.
- Symbols: **flowchart decision tree**, document collection/model service; no invocation-kind filter box.

### Durable events and memory

**`canonical-durable-event-stream` — redesign**

- Components/owners: run producer/RunStreamEntry; provider-selected event stream; shared `RunEvents`; API replicas; browser/MCP subscribers.
- Evidence: `API\Infrastructure\RunStreamStore.cs:183–206`; `EfRunEventStream.cs:19–37,144–167`; `API\Program.cs:1044–1053`.
- PostgreSQL/EF path is durable cursor polling, not process-local fan-out. SQLite compatibility may be a separate inset.
- Symbols: **C4 components + database**, replica containers; product event/run labels only.

**`canonical-durable-event-stream-sequence` — redesign**

- Participants: producer, RunStreamEntry, durable stream/database, independent subscriber replica, SSE consumer.
- Evidence: `EfRunEventStream.cs:68–83,144–167,240–282,409–416`; `Tests\EfRunEventStreamTests.cs:28–54,120–156`.
- Show server-authoritative sequencing/durable append and cursor reads. Retryable assembly-blocked must not imply final stream closure. Existing interface comments describe SQLite/live-channel behavior too broadly (`IRunEventStream.cs:5–10`).
- Symbols: **UML sequence lifelines**, database participant, not a message-broker icon.

**`canonical-memory-context` — redesign**

- Components/owners: project-scoped approved active decisions; nonlegacy agent memory; approved cross-team high-importance memories; latest open session; bounded selector; context compiler; untrusted JSON output.
- Evidence: `API\Memory\MemoryContextCompiler.cs:55–103,110–135,156–181,210–216`; `Tests\Memory\MemoryContextCompilerSecurityTests.cs:23,73,107,135`.
- Inputs converge; core and learning candidates are jointly sorted by importance/recency. Child prompts use decisions-only compilation. “Decisions override everything” must not imply stored text becomes trusted instructions.
- Symbols: **database/table + C4 compiler + document**, not vector database or serial pipeline.

### Email/export architecture identities

**`email-architecture` — retain**

- **Truly survives:** separate client, identity, API/control, execution, data/storage, and external-provider/repository boundaries.
- Evidence: `API\Program.cs:219–227,265–370,1296–1325`; `k8s\base\api-deployment.yaml:338–345`; `worker-deployment.yaml:136–156`; runtime credential path cited above.
- Keep API/worker distinct and repository access from execution accurately qualified. “Email” describes an exported presentation asset, **not an implemented mail subsystem**.
- Symbols: **C4 system/container**, Azure/Kubernetes/cloud/database native symbols.

**`email-components` — retain**

- **Truly survives:** code dependency view, distinct from deployment: API → Data, Domain, SandboxFs, AgentRuntime, Squad; Runtime → AgentTools/SandboxExec/SandboxFs plus Copilot SDK/MAF/governance; AgentHost → Runtime/Domain; MCP is a protocol/API-client surface.
- Evidence: `API\Agentweaver.Api.csproj:4–11,26–29`; `Runtime\Agentweaver.AgentRuntime.csproj:4–16`; `packages\Agentweaver.AgentTools\Agentweaver.AgentTools.csproj:3–5`; `Host\Agentweaver.AgentHost.csproj:4–5`.
- Symbols: **UML package/component or C4 component**; use package/module symbols, not one Kubernetes pod per library.

**`email-coordinator-workflow` — redesign**

- Components/owners: requester, coordinator, durable spec/plan, child executors, collective assembly/review, human reviewer, merge service, final Scribe.
- Evidence: `CoordinatorWorkflowFactory.cs:106–167`; `CoordinatorAssemblyService.cs:504–541,628–658,1695–1770`.
- Separate blocked/recovery from terminal decline/failure; Scribe has finalization paths beyond successful merge. Do not claim verified universal PR publication.
- Symbols: **UML sequence**, product roles/artifacts; no email/SMTP service.

## Seven retained catalog workflows

All seven are embedded YAML definitions, not deployment topologies. **Use flowchart process, decision, and terminal symbols**; distinguish prompt labels from real gate/executor types. Do not insert authored Merge/PR/Scribe nodes.

Shared corroboration: `Tests\Workflows\CatalogWorkflowBindingTests.cs:29–51` inventories gates and excludes authored Merge/Scribe.

| Diagram | Facts that truly survive retain | Canonical evidence |
|---|---|---|
| `workflow-agent-evaluation` | Sequential **Evaluation Setup → Evaluation Runs → Collect Results**, RAI safety gate, report prompt and terminals. No `fan_out`/`fan_in` nodes. | `Catalog\agent_evaluation.yaml:14–59,63–90`. Old “not runnable” statement conflicts: `docs\workflow-library.md:23–29`. Bindability was not executed. |
| `workflow-bug-fix` | Triage/fix prompts; QA **peer_review** Verify; RAI; platform Build/Test gate; human review; revision/decline/safety terminals. | `Catalog\bug_fix.yaml:14–80,84–144`. |
| `workflow-content-authoring` | Research/draft/editorial-review prompts; RAI; human review; **Publish is a prompt**, not a merge executor. | `Catalog\content_authoring.yaml:14–78,82–122`. |
| `workflow-incident-response` | Triage/mitigate/verify prompts; human review; approved postmortem prompt; revision/decline. No authored Scribe. | `Catalog\incident_response.yaml:13–59,63–87`. |
| `workflow-infra-ops` | Plan/implement; DevOps peer validation; RAI; security-engineer infra/config peer review; human review and terminals. | `Catalog\infra_ops.yaml:20–88,92–148`. |
| `workflow-pm-discovery` | Research/synthesis/**Stakeholder Review prompt**; separate actual human-review gate; synthesis revision loop and decline. | `Catalog\pm_discovery.yaml:13–52,56–77`. |
| `workflow-software-delivery` | Plan/implement; QA peer Test Gate; RAI; Rubberduck; **Code Review prompt**; Build/Test; human review; terminals. | `Catalog\software_delivery.yaml:14–96,100–168`. |

**Unverified:** live deployment state, rendered visual quality, current UI pod-pill implementation, and coordinator automatic PR publication. Source/test inspection supports the component maps; it does not establish executed-test or deployed-runtime assurance.
