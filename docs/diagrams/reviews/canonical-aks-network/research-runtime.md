# Astra research: runtime and authority

Completed read-only research thread: `guide-runtime-evidence`, model `gpt-6-astra`.
This is the coordinator's persisted summary of the completed agent result, not a
claim that the agent wrote this file. Scope was launch/readiness/lifetime and
credential authority; no implementation or deployment changes were requested.

## Findings adopted

- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303`:
  persist and bind the claim, probe HTTP reachability, configure synchronously,
  then dispatch. The probe is not a post-configuration readiness loop.
- `AgentHostReadinessProbe.cs:64-79` accepts successful HTTP status before configure.
  AgentHost `Program.cs:258-350,377-407`, `AgentHostRuntimeState.cs:160-173`, and
  `AgentHostStartupService.cs:124-245` distinguish standby HTTP 200 from ready.
  Accepted configuration consumes the one-shot gate before setup; later valid
  attempts return 409 even if that setup failed.
- `apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:101-110,185-208`:
  successful Assistant turns retain the pod; failure/cancellation releases it.
  Active preview defers ordinary cleanup. A retained next turn renews MCP
  authorization rather than configuring the pod again.
- `PodLocalWorkspaceManager.cs:98-137,220-237`: LocalReadOnly verifies source commit
  and tree, with no write-back. It does not mean a physically unwritable filesystem.
  Assistant setup skips project checkout.
- `scripts/azure/steps/15-setup-identity.mjs:245-260,314-423`: API and Worker
  ServiceAccounts federate to the privileged API identity; the distinct AgentHost
  identity has no Key Vault roles. Legacy privileged federation is removed.
- `RunGitHubCapabilityCredentialProvider.cs:78-119` and
  `GitHubCapabilityBroker.cs:81-117`: run/purpose fences bracket retrieval.
  Configured Copilot/BYOK, repository, A2A and Assistant MCP credentials have
  separate purposes; none is ambient browser-user authority.
- API/Worker use CSI files and synced secretKeyRef. API runtime OAuth certificate
  loading is separate (`Program.cs:238-246,905-911`;
  `OAuthServerConfiguration.cs:319-377`). MCP has no secret mounts.

## Audit refinements

Do not turn the audit's readiness wording into an invented second health poll.
Do not draw AgentHost-to-vault access or MCP CSI. These findings drive lifecycle
and credential diagrams and the corresponding architecture prose.
