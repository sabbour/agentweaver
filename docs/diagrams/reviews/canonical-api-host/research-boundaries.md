# Astra boundary research

Separately launched agent: `astra-core-boundaries`, requested model `gpt-6-astra`.
Question: actual component, deployment, persistence and trust boundaries across the owned shard.
No writes or tests were executed by the researcher.

## Reconciled facts

- API web and worker roles share application code, but worker exposes probes only. Background registrations are not universally role-gated; do not claim worker-exclusive orchestration. `apps/Agentweaver.Api/Program.cs:507-532,1255-1326`; `k8s/base/worker-deployment.yaml:88-155`.
- Endpoint integrity, authentication and authorization replace historical token/org prefix gates. Entra tenant validation is inside token validation; project Viewer/Contributor/Owner checks are resource-specific. `Program.cs:1274-1295`; `Auth/EndpointAuthorization.cs:7-30,136-155`; `Auth/EntraAccessTokenValidator.cs:125-131`; `Security/ProjectAuthorization.cs:56-86`.
- MCP validates only broker JWTs, then forwards the accepted token. API owns OpenIddict/consent/PKCE/refresh state and independently authorizes. Assistant receives per-turn broker issuance and renewal, not browser Entra forwarding. `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-86`; `AgentweaverApiClient.cs:359-379`; `apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813`; `RemoteOperatorAssistantAgent.cs:77-83`.
- Postgres selects EF stores/events/checkpoints/CAS leases; local SQLite selects raw operational stores, separate EF memory, local event channels and file checkpoints. Shared workspace files remain distinct. `Program.cs:875-903,1026-1075`; `Infrastructure/EfRunEventStream.cs:18-37,143-167`.
- Children own isolated worktrees/branches. A shared volume is not a shared Git index. `Runs/RunOrchestrator.cs:277-317`.
- App.tsx declares routes; AppShell supplies ProjectListProvider/NotificationsProvider. The static Web host redirects `/docs` externally. API_URL is an origin or empty string, not `/api`. Entra exchange stores the SPA bearer in sessionStorage with transient peer-tab transfer. `apps/web/src/App.tsx:81-127,399-403`; `components/shell/AppShell.tsx:134-182`; `config.ts:13-36,60-72,127-135,207-240`; `apps/Agentweaver.Web/Program.cs:39-65`.
- MAF is in-process graph orchestration; collective review is a service gate, and Operator history is another path. `Coordinator/CoordinatorWorkflowFactory.cs:115-218`; `Coordinator/AssemblyReviewGate.cs:6-19,40-44`.
- Project creation consumes caller-bound repository selection, resolves server-side capability and bootstraps ownership. Local storage accepts a rooted path; persistent-volume storage derives a project-ID path. `Endpoints/ProjectEndpoints.cs:1232-1283`; `Infrastructure/LocalFilesystemWorkspaceProvider.cs:31-43`; `PersistentVolumeWorkspaceProvider.cs:27-35`.
- Skill pointers require successful shared-filesystem materialization; pod-local or failed-write paths inline instructions. Defaults require confirmed team and digest-checked apply. `Skills/SkillPromptComposer.cs:40-123`; `SkillDefaultsService.cs:31-52,231-250`.
- Generation preferences do not authorize provider credentials. `Api.Data/Memory/ProjectRecord.cs:31-33`; `Endpoints/BlueprintEndpoints.cs:140-141`; `Auth/AiExecutionPlanService.cs:293-330,384-402`.
- Repository suggestions are heuristic metadata reads; the registered ambient boundary returns null, and caller cancellation propagates. `Blueprints/GitHubRepoBlueprintSuggestionService.cs:40-115`; `Auth/EntraOnlyGitHubCredentialBoundary.cs:38-39`.
- Agent-definition generator has five outputs; materialization does not overwrite detected existing files. `scripts/gen-docs.mjs:266-273`; `Projects/AgentDefinitionTemplate.cs:50-60`.
- Test seams do not prove live auth/model/Kubernetes execution. Real-process MCP is loopback with controlled issuer/API; Postgres Testcontainers are separate from SQLite EF tests. Frontend Vitest is a separate layer. `tests/Agentweaver.Tests/Helpers/AgentweaverWebApplicationFactory.cs:22-63`; `ProjectsWebApplicationFactory.cs:92-96,170-201`; `Mcp/McpBrokerRealProcessTests.cs:235-255`; `Auth/OpenIddictAuthorizationServerTests.cs:196-239`; `tests/e2e/playwright.config.ts:11-15`.

## Limits

Configured manifests are not live-cluster observations; test sources are not passing test results.
Shared canonical readiness belongs to its owner. The flow researcher additionally distinguished
local RunStreamStore snapshots from durable-provider polling; neither is universally the other.
Native process/UML/database/cloud symbols are suitable for this host; Azure/Kubernetes assets
must use their native libraries where those actual resources are depicted.
