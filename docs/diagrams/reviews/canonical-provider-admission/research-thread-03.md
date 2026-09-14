# Shared research thread 3: assurance and cleanup

Model: GPT-6 Astra (`gpt-6-astra`). Separately launched, read-only research agent
`shared-assurance-research`, after independent component and flow threads.
This is the coordinator's preserved abridgment of its returned findings, not a
claim that tests were executed by that researcher.

The researcher inspected implementation, configuration, tests and current documentation
for all 26 existing retain/redesign survivors and proposed provider admission.
Legacy graphics were not evidence. No nested agents or edits were used.

## Reconciled findings

- Authentication selects by endpoint metadata, not GitHubLegacy or raw GitHub tokens.
  Issuer inspection routes a bearer; validation follows. Broker eligibility includes
  `AuthenticatedSelfOrMcp`. Authentication does not replace persisted-resource
  authorization. Evidence: `AgentweaverAuthentication.cs:21-59,204-249`,
  `EndpointHelpers.cs:116-153`, `ProjectAuthorization.cs:57-96`;
  `AuthenticationSchemeCutoverTests.cs:70,99,123,157`,
  `ProjectRunAuthorizationTests.cs:291,389,445,458,654`.
- MCP requires the configured issuer, one exact resource audience, keyed RS256,
  lifetime, subject and `mcp:invoke`; it forwards the validated broker token.
  Evidence: `McpBrokerAuthenticationHandler.cs:66-80`,
  `AgentweaverApiClient.cs:332-374`, `OpenIddictAuthorizationServerTests.cs`.
- Assistant turns use renewed API-issued broker credentials, not caller Entra bearers.
  Held pods can span turns; provider changes release the provider-bound pod.
  Idle is CAS-resumable; Completed/run.completed seals rehydration.
  Evidence: `AssistantRunService.cs:604-609,789-813,906-931`,
  `RemoteOperatorAssistantAgent.cs:107-110,319-336`,
  `AssistantRunConcurrencyAndPodLifecycleTests.cs:108,150,176,218,253`.
- Provider preparation, acceptance, revalidation and durable snapshots are distinct.
  Five-minute keys bind operation/project/subject/provider. Invalid or changed keys
  reject with replacement context. Database ownership identifies the winning private
  provider snapshot; secrets are not public run-event data.
  Evidence: `AiExecutionPlanService.cs:206,256-358,372-458`,
  `RunModelProviderSnapshotStore.cs:12-15,160-209`,
  `AiExecutionContextEndpointsTests.cs:355,368,413,574`.
- Current execution DOES receive repository credentials. API `/configure` delivery,
  Host memory and runtime tool options support restricted credential-bearing direct
  `git status` and allowlisted `gh`; ordinary shell commands do not inherit them
  indiscriminately. This conflicts with the normative two-App contract. Copilot
  material is separately purpose/run-bound and expiry checked.
  Evidence: `KubernetesSandboxExecutor.cs:697-705,1270-1279`,
  `AgentHostRuntimeState.cs:172-174`, `CopilotAIAgent.cs:452`,
  `RunCommandTool.cs:230-343`, `AssemblyBuildTestShellGuardTests.cs:156-305`.
- Capability redemption fences both before and after mint/read. Retry/child snapshot
  inheritance has explicit lifecycle exceptions; do not claim grants never refresh.
  Evidence: `GitHubCapabilityBroker.cs:41-117`,
  `RunGitHubCapabilitySnapshotLifecycle.cs:24-119`,
  `GitHubConnectionsPersistenceStoreTests.cs:467,548,661,688,796`.
- Tool dispatch is not one universal evaluator chain. Native shell denial, URL
  approvals and custom reporting bypass differ; governed calls combine AGT and
  direct containment, failing closed. Evidence: `CopilotAIAgent.cs:1857-2116`,
  `SandboxGovernance.cs:117-178`, `PermissionDecisionRegressionTests.cs`.
- Shared and verified pod-local workspace modes coexist. Worktree identity and pod
  lifetime are distinct. Preview publication probes the exact generated HTTPS URL;
  it does not TCP-probe the preview pod from the API. Failure rolls resources back.
  Evidence: `SandboxPreviewService.cs:201-211,335-444`,
  `SandboxPreviewPublicationTests.cs:22,59,145,181,205,224`.
- AKS manifest counts are API 2, frontend 2, MCP 1, worker 2/HPA 2-3.
  API and worker use Postgres and CSI; MCP does not have that CSI mount.
  AgentHost has a separate KV-less identity; app and preview gateways differ.
  Evidence: `k8s/base/{api,worker,frontend,mcp}-deployment.yaml`,
  `worker-hpa.yaml:65-66`, `sandbox-template-agenthost.yaml:139-163`.
  These are manifest facts, not live deployment observations.
- Communication is dependency-frontier handoff, not arbitrary peer messaging.
  Failed/RAI-flagged dependencies block dependents. Board columns project persisted
  states; blocked Ready is metadata, approval alone is not Done. All-promoted
  classification keeps everything inline. Evidence: `CoordinatorDispatchService.cs`,
  `SubtaskFrontierTests.cs`, `WorkflowStageProjector.cs:54-80`,
  `CoordinatorOrchestratorExecutor.cs:1722-1740`,
  `PrdStoryPromotionPartitionTests.cs`.
- Collective assembly does not establish universal PR publication. Actual local
  MergeWorktree proceeds to Scribe; decline deliberately skips Scribe, while merge
  failure can invoke it. The separate default workflow explicitly has `push-pr`.
  Evidence: `CollectiveAssemblyPipeline.cs:439-467`,
  `CoordinatorAssemblyService.cs:1695-1758,1879-1882`,
  `CoordinatorAssemblyServiceTests.cs:2733-2822`,
  `DefaultWorkflowTemplate.cs:85-93`.
- EF/Postgres is a shared durable cursor relay, not a cross-replica process channel.
  Append commits before acknowledgment; advisory lock plus MAX+1 allocates sequence.
  Duplicate explicit sequence requires identical contents. Poll after cursor at
  approximately 250 ms; drain terminal-containing batches. Retryable assembly_blocked
  is not terminal. Late-delta suppression is process-local, not a database fence.
  Evidence: `EfRunEventStream.cs:18-48,59-86,144-189,223-284,409-427`,
  `EfRunEventStreamTests.cs`, `RunEventStreamPostgresTests.cs`.
- Memory inputs converge; candidates sort importance then recency. Memory selection
  bounds do not cap all decisions/session context. Output is untrusted JSON.
  Evidence: `MemoryContextCompiler.cs:56-143,182-223`,
  `MemoryContextCompilerSecurityTests.cs:23,73,107,135`.
- Workflow generation validates loader and binder, permits one correction, then
  fails explicitly. Generation is not save. Selection is trigger-agnostic;
  explicit/conversational overrides precede singleton; parse retries are bounded,
  model exceptions fall back immediately. Evidence: `CopilotWorkflowGenerator.cs`,
  `WorkflowGeneratorTests.cs:309,328,341,622`,
  `CoordinatorOrchestratorExecutor.cs:288-372`, `WorkflowSelector.cs:83-218`.

## Retained catalog topology

Current YAML and legacy graph JSON have equal `(from,to,label)` sets for all seven:
evaluation 8, bug-fix 15, content 11, incident 7, infra 14, PM discovery 6,
software delivery 17. Evaluation is sequential prompts, not fan-out/fan-in.
Edit/publish, stakeholder review, incident verification and software code-review
are prompts where declared. Gate verdict names and all revision/failure edges matter.
`CatalogWorkflowBindingTests` covers gate inventory; its bindability theory omits
evaluation. Topology comparison is not execution of every verdict branch.

## Cleanup evidence and limits

Completed tracked/nonignored text scans found no concrete non-Markdown consumer for
the nine orphan families: a2a-bridge-fig2, aks-block-diagram, auth-security-fig2,
auth-security-fig3, browser-console-fig1, events-observability-fig3,
git-integration-fig3, landing-product-feature, token-usage-monitoring-fig1.
Audit/inventory and asset self-references were excluded as consumers.
The first broad filesystem scan timed out; the completed bounded scans define this result.
Known Markdown consumers still gate canonical-event-replay-tail removal.
Ignored files, external consumers, live deployment and final visual quality remain
outside this research evidence.
