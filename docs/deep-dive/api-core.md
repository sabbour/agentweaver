# API Host Core — Deep Dive

## Purpose & Scope

This deep dive documents the **Agentweaver.Api host core**: ASP.NET bootstrap, dependency injection, cross-cutting middleware, endpoint registration, core contracts, configuration, diagnostics, metrics, and infrastructure seams. It intentionally does **not** deep-dive domain logic owned elsewhere:

- Auth/Security: see [`auth-security.md`](auth-security.md). This file cites only the custom middleware positions and exemptions needed to understand host behavior.
- Coordinator, Workflows, Runs, Backlog, ReviewPolicies, Casting, Blueprints: see [`orchestration.md`](orchestration.md). This file catalogs their routes and DI registration but does not explain their internals.
- Sandbox: see [`sandbox.md`](sandbox.md). This file covers only sandbox registration and policy-store plumbing.
- Memory, Migrations, Git: see [`data-persistence.md`](data-persistence.md). This file covers only host startup and store wiring.

Source orientation: `Agentweaver.Api` is the ASP.NET Core backend for an AI file-editing agent platform, using .NET/ASP.NET Core, LibGit2Sharp, and SQLite (`apps/Agentweaver.Api/README.md:1-15`). Its public API reference states `/api` is the base path and bearer auth is required for API endpoints (`apps/Agentweaver.Api/API.md:1-13`), though several health/OAuth bootstrap paths are custom-middleware exemptions described below.

## Host Bootstrap (Program.cs walkthrough: builder, services, app, run)

1. **Builder creation.** `WebApplication.CreateBuilder(args)` creates the default ASP.NET Core host, configuration, logging, and environment pipeline (`apps/Agentweaver.Api/Program.cs:31`).
2. **Fail-fast security guards.** Before service setup, production startup refuses known-dangerous test auth bypasses and enforces pinned OAuth issuer/audience for production MCP/API JWT validation (`apps/Agentweaver.Api/Program.cs:33-43`).
3. **JSON output policy.** HTTP JSON uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (`apps/Agentweaver.Api/Program.cs:46-49`). Separately, `JsonDefaults` uses null omission, the same relaxed encoder, `ModelSourceJsonConverter`, and camelCase enum names for shared event/API serialization (`apps/Agentweaver.Api/Contracts/JsonDefaults.cs:7-29`).
4. **CORS.** The default CORS policy reads `Cors:AllowedOrigins`, allows only configured origins, and permits any header/method (`apps/Agentweaver.Api/Program.cs:51-60`).
5. **Core services.** Infrastructure, stream stores, workflow runtime, project/workspace providers, diagnostics, metrics, auth, memory EF, sandbox routing, and domain services are registered before `builder.Build()` (`apps/Agentweaver.Api/Program.cs:62-261`).
6. **Build.** `var app = builder.Build();` materializes the host (`apps/Agentweaver.Api/Program.cs:263`).
7. **Main SQLite boot.** `SqliteDb.EnsureCreatedAsync()` creates/patches the operational SQLite schema before serving traffic (`apps/Agentweaver.Api/Program.cs:265`; schema owner in `apps/Agentweaver.Api/Infrastructure/SqliteDb.cs:53-157`).
8. **Memory EF boot.** A scoped `MemoryDbContext` runs SQLite WAL/busy-timeout setup, handles a pre-migration transition case, seeds EF migration history if needed, and always calls `MigrateAsync()` (`apps/Agentweaver.Api/Program.cs:267-325`).
9. **Recovery.** The app recovers interrupted generic workflows, warns on unhealthy workspace mounts, recovers coordinator runs, and runs a coordinator reconciler sweep before middleware (`apps/Agentweaver.Api/Program.cs:326-351`).
10. **Middleware.** Exception handler, CORS, rate limiter, GitHub token auth, and GitHub org authorization are added in that order (`apps/Agentweaver.Api/Program.cs:353-364`).
11. **Endpoint registration.** Endpoint extension methods are mapped in a fixed order from runs/projects through diagnostics, metrics, and sandbox (`apps/Agentweaver.Api/Program.cs:365-382`).
12. **Run.** `app.Run()` starts Kestrel; `public partial class Program { }` enables integration-test entry-point access (`apps/Agentweaver.Api/Program.cs:384-386`).

## Dependency Injection Map

| service interface | implementation | lifetime | registered at |
|---|---|---:|---|
| HTTP JSON options | configured inline | options | `apps/Agentweaver.Api/Program.cs:46` |
| CORS default policy | configured inline from `Cors:AllowedOrigins` | policy | `apps/Agentweaver.Api/Program.cs:56` |
| `SqliteDb` | `SqliteDb` | Singleton | `apps/Agentweaver.Api/Program.cs:63` |
| `SqliteRunStore` | `SqliteRunStore` | Singleton | `apps/Agentweaver.Api/Program.cs:64` |
| `SqliteRunRevisionStore` | `SqliteRunRevisionStore` | Singleton | `apps/Agentweaver.Api/Program.cs:65` |
| `SqliteWorkflowRunStore` | `SqliteWorkflowRunStore` | Singleton | `apps/Agentweaver.Api/Program.cs:66` |
| `ISandboxPolicyStore` | `YamlSandboxPolicyStore` | Singleton | `apps/Agentweaver.Api/Program.cs:67` |
| `RunStreamStore` | `RunStreamStore` | Singleton | `apps/Agentweaver.Api/Program.cs:68` |
| `IRunEventStream` | `SqliteRunEventStream` | Singleton | `apps/Agentweaver.Api/Program.cs:72` |
| `WorktreeManager` | `WorktreeManager` | Singleton | `apps/Agentweaver.Api/Program.cs:73` |
| `RepositoryMergeLock` | `RepositoryMergeLock` | Singleton | `apps/Agentweaver.Api/Program.cs:74` |
| `RunWorkflowRegistry` | `RunWorkflowRegistry` | Singleton | `apps/Agentweaver.Api/Program.cs:77` |
| `PendingRequestStore` | `PendingRequestStore` | Singleton | `apps/Agentweaver.Api/Program.cs:78` |
| `IWorktreeOperations` | `WorktreeOperationsAdapter` | Singleton | `apps/Agentweaver.Api/Program.cs:79` |
| `IMergeCoordinator` | `MergeCoordinator` | Singleton | `apps/Agentweaver.Api/Program.cs:80` |
| `RunWorkflowFactory` | `RunWorkflowFactory` | Singleton | `apps/Agentweaver.Api/Program.cs:81` |
| `RunWatchLoopService` | `RunWatchLoopService` | Singleton | `apps/Agentweaver.Api/Program.cs:82` |
| `WorkflowRestartService` | `WorkflowRestartService` | Singleton | `apps/Agentweaver.Api/Program.cs:83` |
| Coordinator services | concrete/interface registrations | Singleton | `apps/Agentweaver.Api/Program.cs:85-109` |
| GitHub auth services | token store, scope provider, refresh/device/redirect services | Singleton/HttpClient | `apps/Agentweaver.Api/Program.cs:111-121` |
| MCP OAuth services | token broker/session exchange/client and refresh stores | Singleton/Scoped | `apps/Agentweaver.Api/Program.cs:123-133` |
| OAuth rate limiter policy | fixed window, 20 req/min/IP | policy | `apps/Agentweaver.Api/Program.cs:139-155` |
| `SqliteProjectStore` | `SqliteProjectStore` | Singleton | `apps/Agentweaver.Api/Program.cs:158` |
| `IProjectStore` | `SqliteProjectStore` | Singleton alias | `apps/Agentweaver.Api/Program.cs:159` |
| `LocalFilesystemWorkspaceProvider` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:160` |
| `PersistentVolumeWorkspaceProvider` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:161` |
| `IProjectWorkspaceProvider` | config-selected local or persistent-volume provider | Singleton factory | `apps/Agentweaver.Api/Program.cs:162-171` |
| `ProjectGitInitializer` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:172` |
| `ProjectService` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:173` |
| Backlog/board services | store, projector, board, pickup/reconciler | Singleton/Hosted | `apps/Agentweaver.Api/Program.cs:175-185` |
| `WorkflowRegistry` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:188` |
| `ReviewPolicyRegistry` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:189` |
| `DiagnosticsService` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:190` |
| `MetricsService` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:191` |
| Agent runtime package services | `AddAgentRuntime()` | extension-defined | `apps/Agentweaver.Api/Program.cs:193-194` |
| `IPodNameRegistry` | `PodNameRegistry` | Singleton | `apps/Agentweaver.Api/Program.cs:198` |
| `ISandboxExecutorRouter` | `SandboxExecutorRouter` | Singleton | `apps/Agentweaver.Api/Program.cs:199` |
| `ISandboxExecutor` | router-resolved executor | Singleton factory | `apps/Agentweaver.Api/Program.cs:200-201` |
| `PortForwardService` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:204` |
| `IMemoryCache` | ASP.NET memory cache | Singleton | `apps/Agentweaver.Api/Program.cs:207` |
| `IGitHubOrgAuthorizationService` | `GitHubOrgAuthorizationService` | Singleton | `apps/Agentweaver.Api/Program.cs:208` |
| `RepositoryRootValidator` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:211` |
| `MemoryDbContext` | EF Core SQLite/SQL Server/PostgreSQL by config | Scoped | `apps/Agentweaver.Api/Program.cs:215-239` |
| `MemoryContextCompiler` | concrete | Scoped | `apps/Agentweaver.Api/Program.cs:240` |
| `PostRunScribeService` | concrete | Scoped | `apps/Agentweaver.Api/Program.cs:241` |
| `ProjectWorkspaceService` | concrete | Singleton | `apps/Agentweaver.Api/Program.cs:242` |
| `CheckpointGcService` | hosted background service | Hosted | `apps/Agentweaver.Api/Program.cs:245` |
| Casting/blueprint/workflow-generation/decompose services | domain services | Singleton | `apps/Agentweaver.Api/Program.cs:247-261` |

## Middleware Pipeline

Ordered pipeline (`Program.cs`):

1. Exception handler writes a generic JSON 500 (`apps/Agentweaver.Api/Program.cs:353-358`).
2. CORS (`apps/Agentweaver.Api/Program.cs:360`).
3. ASP.NET rate limiter (`apps/Agentweaver.Api/Program.cs:361`); only endpoints with `RequireRateLimiting(OAuthServerEndpoints.RateLimitPolicy)` are limited, notably `/oauth/authorize`, `/oauth/token`, `/oauth/register`, and `/oauth/revoke` (`apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:70-128`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:131-243`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:250-338`).
4. `GitHubTokenAuthMiddleware` validates `/api/*` bearer tokens, except non-API paths, `/api/ping`, `/api/health`, and `/api/auth/session/exchange` (`apps/Agentweaver.Api/Program.cs:362`; `apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:48-62`, `apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:134-143`).
5. `GitHubOrgAuthorizationMiddleware` must run after token auth and enforces allowed org/team membership (`apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationMiddleware.cs:6-16`). Its exempt prefixes are `/health`, `/healthz`, `/api/health`, `/api/ping`, `/auth`, `/api/auth`, `/mcp`, `/oauth`, and `/.well-known` (`apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationMiddleware.cs:25-39`).
6. Endpoint handlers execute after both auth gates (`apps/Agentweaver.Api/Program.cs:365-382`).

```mermaid
flowchart TD
    A[Request] --> B[Exception handler]
    B --> C[CORS]
    C --> D[UseRateLimiter]
    D --> E{GitHubTokenAuthMiddleware}
    E -->|non-/api, /api/ping, /api/health, /api/auth/session/exchange| F{GitHubOrgAuthorizationMiddleware}
    E -->|/api/* bearer valid| F
    E -->|missing/invalid bearer| U[401]
    F -->|exempt prefix| H[Endpoint]
    F -->|non-exempt + caller/org valid| H
    F -->|no caller| U
    F -->|org denied or unconfigured| X[403]
    H --> S{SSE endpoint?}
    S -->|/api/runs/{id}/stream| L[RunStreamStore live replay + IRunEventStream durable fallback]
    S -->|other| R[JSON/NoContent/etc.]
```

SSE is implemented by `GET /api/runs/{id}/stream` (`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:314`). It authorizes against in-memory stream ownership or persisted run ownership, uses `Last-Event-ID`, writes `text/event-stream`, replays durable events through `IRunEventStream` when no live stream entry exists, emits a legacy `agent.message` fallback for old completed rows, and always ends with a synthetic `done` event (`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:346-443`). Live streaming polls `RunStreamEntry.GetSnapshotSince`, closes at terminal completion or the `review.requested` HITL gate, and writes SSE events through `EndpointHelpers.WriteSseEventAsync` (`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:447-500`; `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:31-43`).

## Endpoint Catalog

Auth requirement shorthand:

- **Public/exempt**: custom middleware permits the route without a bearer token and without org gate.
- **Bearer**: `/api/*` bearer token validated by `GitHubTokenAuthMiddleware`; org gate may be exempt by prefix.
- **Bearer + org**: token validation plus GitHub org authorization.
- **Owner**: handler performs project/run ownership checks after global auth.
- **Domain-owned**: route is cataloged here but details belong to the linked domain deep dive.

| route | verb | handler file | purpose | auth requirement |
|---|---:|---|---|---|
| `/` | GET | `Endpoints/RunEndpoints.cs:31` | Root banner string | Non-API but not org-exempt; unauthenticated callers hit org gate (gotcha) |
| `/health` | GET | `Diagnostics/DiagnosticsEndpoints.cs:19` | Lightweight reachability | Public/exempt |
| `/api/health` | GET | `Diagnostics/DiagnosticsEndpoints.cs:23` | Gateway/Kubernetes readiness alias | Public/exempt |
| `/api/ping` | GET | `Diagnostics/DiagnosticsEndpoints.cs:26` | Lightweight liveness | Public/exempt |
| `/healthz/workspace` | GET | `Diagnostics/DiagnosticsEndpoints.cs:32` | Workspace mount readiness | Public/exempt |
| `/api/diagnostics` | GET | `Diagnostics/DiagnosticsEndpoints.cs:43` | Global system diagnostics | Bearer + org |
| `/api/diagnostics/heartbeat` | GET | `Diagnostics/DiagnosticsEndpoints.cs:53` | Coordinator heartbeat snapshot | Bearer + org |
| `/api/projects/{id}/diagnostics` | GET | `Diagnostics/DiagnosticsEndpoints.cs:58` | Project diagnostics | Bearer + org + Owner |
| `/api/projects/{id}/dashboard` | GET | `Endpoints/MetricsEndpoints.cs:20` | Project dashboard metrics | Bearer + org + Owner |
| `/api/overview` | GET | `Endpoints/MetricsEndpoints.cs:42` | Global now/overview metrics | Bearer + org |
| `/api/server/info` | GET | `Endpoints/ProjectEndpoints.cs:200` | Data directory and workspace-provider metadata | Declared `AllowAnonymous`, but custom `/api` middleware still requires Bearer + org |
| `/api/projects` | POST | `Endpoints/ProjectEndpoints.cs:34` | Create project | Bearer + org |
| `/api/projects` | GET | `Endpoints/ProjectEndpoints.cs:207` | List caller-owned projects | Bearer + org |
| `/api/projects/{id}` | GET | `Endpoints/ProjectEndpoints.cs:219` | Get one project | Bearer + org + Owner |
| `/api/projects/{id}` | PATCH | `Endpoints/ProjectEndpoints.cs:235` | Rename project | Bearer + org + Owner |
| `/api/projects/{id}/provider-settings` | PUT | `Endpoints/ProjectEndpoints.cs:259` | Update model/provider defaults | Bearer + org + Owner |
| `/api/projects/{id}/relink` | POST | `Endpoints/ProjectEndpoints.cs:289` | Relink moved workspace | Bearer + org + Owner |
| `/api/projects/{id}` | DELETE | `Endpoints/ProjectEndpoints.cs:314` | Record-only delete with `confirm=true` | Bearer + org + Owner |
| `/api/projects/{id}/workspace/refs` | GET | `Endpoints/ProjectWorkspaceEndpoints.cs:18` | List browsable refs | Bearer + org + Owner via service |
| `/api/projects/{id}/workspace` | GET | `Endpoints/ProjectWorkspaceEndpoints.cs:37` | List files for ref | Bearer + org + Owner via service |
| `/api/projects/{id}/workspace/files/{**path}` | GET | `Endpoints/ProjectWorkspaceEndpoints.cs:57` | File content when path ends `/content` | Bearer + org + Owner via service |
| `/api/runs` | POST | `Endpoints/RunEndpoints.cs:33` | Start standalone run | Bearer + org |
| `/api/runs/{id}` | GET | `Endpoints/RunEndpoints.cs:100` | Get run state/diff | Bearer + org + Owner |
| `/api/runs/{id}/archive` | POST | `Endpoints/RunEndpoints.cs:228` | Archive run | Bearer + org + Owner |
| `/api/runs/{id}` | DELETE | `Endpoints/RunEndpoints.cs:256` | Delete run row/stream | Bearer + org + Owner |
| `/api/runs/{id}/stream` | GET | `Endpoints/RunEndpoints.cs:314` | SSE event stream | Bearer + org + Owner; non-owner may get 404 |
| `/api/runs/{id}/events` | GET | `Endpoints/RunEndpoints.cs:508` | Persisted RunEvents JSON | Bearer + org + Owner |
| `/api/runs/{id}/graph` | GET | `Endpoints/RunEndpoints.cs:553` | Dynamic workflow graph | Bearer + org + Owner |
| `/api/runs/{id}/history` | GET | `Endpoints/RunEndpoints.cs:610` | Copilot session history for terminal runs | Bearer + org + Owner |
| `/api/runs/{id}/review` | POST | `Endpoints/RunEndpoints.cs:685` | Human review decision | Bearer + org + Owner |
| `/api/runs/{id}/commit` | POST | `Endpoints/RunEndpoints.cs:864` | Commit/merge approved run | Bearer + org + Owner |
| `/api/runs/{id}/request-changes` | POST | `Endpoints/RunEndpoints.cs:1035` | Ask agent to revise | Bearer + org + Owner |
| `/api/runs/{id}/retry` | POST | `Endpoints/RunEndpoints.cs:1179` | Retry failed run | Bearer + org + Owner |
| `/api/runs/{id}/workspace` | GET | `Endpoints/RunEndpoints.cs:1309` | Run worktree listing | Bearer + org + Owner |
| `/api/runs/{id}/shell-approvals` | POST | `Endpoints/RunEndpoints.cs:1463` | Approve pending shell request | Bearer + org + Owner |
| `/api/runs/{id}/shell-denials` | POST | `Endpoints/RunEndpoints.cs:1487` | Deny pending shell request | Bearer + org + Owner |
| `/api/runs/{id}/tool-approvals` | POST | `Endpoints/RunEndpoints.cs:1511` | Approve pending tool request | Bearer + org + Owner |
| `/api/runs/{id}/tool-denials` | POST | `Endpoints/RunEndpoints.cs:1545` | Deny pending tool request | Bearer + org + Owner |
| `/api/runs/{id}/questions/{requestId}/answer` | POST | `Endpoints/RunEndpoints.cs:1572` | Answer HITL question | Bearer + org + Owner |
| `/api/runs/{id}/auto-approve` | POST | `Endpoints/RunEndpoints.cs:1603` | Toggle auto-approve tools | Bearer + org + Owner |
| `/api/runs/{id}/autopilot` | POST | `Endpoints/RunEndpoints.cs:1624` | Toggle autopilot | Bearer + org + Owner |
| `/api/sandbox-policy` | GET | `Endpoints/RunEndpoints.cs:1645` | Read sandbox policy for repo path | Bearer + org + repository owner check |
| `/api/sandbox-policy` | PUT | `Endpoints/RunEndpoints.cs:1670` | Update sandbox policy | Bearer + org + repository owner check |
| `/api/runs/{id}/files` | GET | `Endpoints/RunEndpoints.cs:1704` | Changed-file set | Bearer + org + Owner |
| `/api/runs/{id}/files/{**path}` | GET | `Endpoints/RunEndpoints.cs:1790` | Per-file diff or content suffix | Bearer + org + Owner |
| `/api/projects/{id}/runs` | GET | `Endpoints/ProjectEndpoints.cs:348` | List project workflow runs | Bearer + org + Owner |
| `/api/projects/{id}/runs/{workflowRunId}` | GET | `Endpoints/ProjectEndpoints.cs:410` | Get workflow run summary | Bearer + org + Owner |
| `/api/projects/{id}/runs` | POST | `Endpoints/ProjectEndpoints.cs:458` | Start project run | Bearer + org + Owner |
| `/api/projects/{id}/orchestrations` | POST | `Endpoints/ProjectEndpoints.cs:630` | Start coordinator orchestration | Bearer + org + Owner; domain-owned |
| `/auth/github/authorize` | GET | `Endpoints/AuthEndpoints.cs:31` | Begin web GitHub OAuth redirect | Public/exempt |
| `/auth/github/callback` | GET | `Endpoints/AuthEndpoints.cs:49` | Web or MCP GitHub OAuth callback | Public/exempt |
| `/api/auth/session/exchange` | POST | `Endpoints/AuthEndpoints.cs:109` | Redeem one-time web sign-in code | Public/exempt |
| `/api/auth/github/device` | POST | `Endpoints/AuthEndpoints.cs:120` | Start GitHub device flow | Bearer; org exempt |
| `/api/auth/github/poll` | POST | `Endpoints/AuthEndpoints.cs:153` | Poll device flow | Bearer; org exempt |
| `/api/auth/github` | GET | `Endpoints/AuthEndpoints.cs:177` | Current auth status | Bearer; org exempt |
| `/api/github/accounts` | GET | `Endpoints/AuthEndpoints.cs:204` | GitHub user/org accounts | Bearer + org |
| `/api/github/repos` | GET | `Endpoints/AuthEndpoints.cs:295` | GitHub repositories | Bearer + org |
| `/api/auth/github/sign-out` | POST | `Endpoints/AuthEndpoints.cs:372` | Sign out token scope | Bearer; org exempt |
| `/.well-known/oauth-authorization-server` | GET | `Endpoints/OAuthServerEndpoints.cs:50` | OAuth AS metadata | Public/exempt |
| `/.well-known/oauth-authorization-server/mcp` | GET | `Endpoints/OAuthServerEndpoints.cs:51` | MCP-scoped AS metadata | Public/exempt |
| `/.well-known/openid-configuration` | GET | `Endpoints/OAuthServerEndpoints.cs:53` | OIDC discovery alias | Public/exempt |
| `/.well-known/openid-configuration/mcp` | GET | `Endpoints/OAuthServerEndpoints.cs:54` | MCP OIDC discovery alias | Public/exempt |
| `/oauth/jwks` | GET | `Endpoints/OAuthServerEndpoints.cs:57` | Public signing key | Public/exempt |
| `/oauth/authorize` | GET | `Endpoints/OAuthServerEndpoints.cs:70` | PKCE auth-code start | Public/exempt + OAuth rate-limited |
| `/oauth/token` | POST | `Endpoints/OAuthServerEndpoints.cs:131` | Code/refresh token exchange | Public/exempt + OAuth rate-limited |
| `/oauth/register` | POST | `Endpoints/OAuthServerEndpoints.cs:250` | Dynamic client registration | Public/exempt + OAuth rate-limited |
| `/oauth/revoke` | POST | `Endpoints/OAuthServerEndpoints.cs:312` | Token revocation | Public/exempt + OAuth rate-limited |
| `/api/projects/{id}/team` and `/team/*` | GET/POST/PUT/PATCH/DELETE | `Endpoints/TeamEndpoints.cs:31-422` | Team member/charter/history/sync APIs | Bearer + org + Owner; orchestration/casting-owned |
| `/api/casting/*`, `/api/catalog/roles` | mixed | `Endpoints/CastingEndpoints.cs:31-278` | Casting proposal/catalog APIs | Bearer + org; domain-owned |
| `/api/blueprints*` | GET/POST | `Endpoints/BlueprintEndpoints.cs:15-50` | Blueprint list/generate/validate | Bearer + org; domain-owned |
| `/api/projects/{id}/backlog*`, `/board`, `/workflow-stages` | mixed | `Endpoints/BacklogEndpoints.cs:19-345` | Backlog and board APIs | Bearer + org + Owner; orchestration-owned |
| `/api/projects/{id}/workspace/files`, `/backlog/decompose` | GET/POST | `Endpoints/BacklogDecomposeEndpoints.cs:66-86` | Workspace file tree and spec decomposition | Bearer + org + Owner; orchestration-owned |
| `/api/runs/{id}/outcome-spec*`, `/work-plan`, `/children`, `/steer`, `/assembly/*` | mixed | `Endpoints/CoordinatorEndpoints.cs:31-473` | Coordinator outcome/work-plan/assembly APIs | Bearer + org + Owner; orchestration-owned |
| `/api/projects/{id}/decisions*` | mixed | `Endpoints/DecisionsEndpoints.cs:35-322` | Decision inbox/decision ledger | Bearer + org; data-persistence-owned |
| `/api/projects/{id}/memory*`, `/agents/{name}/memory*`, `/sessions*` | mixed | `Endpoints/MemoryEndpoints.cs:31-405` | Memory/session/file interop | Bearer + org; data-persistence-owned |
| `/api/runs/{runId}/sandbox/port-forward*` | GET/POST/DELETE | `Endpoints/SandboxEndpoints.cs:15-95` | Port-forward sessions | Bearer + org + Owner; sandbox-owned |
| `/api/projects/{projectId}/workflows*` | mixed | `Workflows/WorkflowDefinitionEndpoints.cs:21-313` | Workflow definition APIs | Bearer + org + Owner; orchestration-owned |
| `/api/projects/{projectId}/review-policies*` | mixed | `ReviewPolicies/ReviewPolicyEndpoints.cs:19-68` | Review policy APIs | Bearer + org + Owner; orchestration-owned |

## Contracts / DTOs

The shared contract layer is mostly `apps/Agentweaver.Api/Contracts/Dtos.cs`, with explicit `JsonPropertyName` attributes rather than a global member naming policy (`apps/Agentweaver.Api/Contracts/JsonDefaults.cs:7-13`). Key shapes:

- **Run creation/response.** `CreateRunRequest` accepts `repository_path`, `originating_branch`, `task`, `model_source`, optional `agent_name`, and `auto_approve_tools` (`apps/Agentweaver.Api/Contracts/Dtos.cs:5-30`). `CreateRunResponse` returns `run_id`, `workflow_run_id`, and `status` (`apps/Agentweaver.Api/Contracts/Dtos.cs:32-43`). `RunResponse` is the large run projection with status, model source, timings, result/diff, sandbox, worktree branch, outcome self-assessment, coordinator metadata, flags, and archive time (`apps/Agentweaver.Api/Contracts/Dtos.cs:45-184`).
- **Run and backlog enum strings.** `RunStatusExtensions` maps domain statuses to snake_case API strings such as `awaiting_review`, `merge_failed`, and `assemble_ready` (`apps/Agentweaver.Api/Contracts/RunStatusExtensions.cs:5-38`). `RunOriginExtensions` maps `interactive` and `backlog_pickup` (`apps/Agentweaver.Api/Contracts/RunOriginExtensions.cs:5-20`). `BacklogTaskStateExtensions` maps `backlog`, `ready`, and `claimed` (`apps/Agentweaver.Api/Contracts/BacklogTaskStateExtensions.cs:5-22`).
- **Sandbox and approvals.** `SandboxStatusDto`, `SandboxPolicyDto`, and partial-update `SandboxPolicyUpdateRequest` define sandbox status/policy wire shapes (`apps/Agentweaver.Api/Contracts/Dtos.cs:242-322`). Shell/tool approval and question-answer requests are at `apps/Agentweaver.Api/Contracts/Dtos.cs:330-359`.
- **Review/workspace artifacts.** `ReviewRequest`, `AssemblyReviewRequest`, `ReviewResponse`, file-entry/diff/content DTOs, commit response, and request-changes response live at `apps/Agentweaver.Api/Contracts/Dtos.cs:361-524`.
- **Projects.** `CreateProjectRequest` includes project metadata plus optional blueprint inputs and generated workflow YAML (`apps/Agentweaver.Api/Contracts/Dtos.cs:530-552`). `ProjectResponse` returns project id, origin, source repository, workspace, default branch, owner, provider defaults, state, timestamps, blueprint provenance, and allowed workflows (`apps/Agentweaver.Api/Contracts/Dtos.cs:571-590`). `CreateProjectRunRequest` accepts task/model/base-branch/agent fields (`apps/Agentweaver.Api/Contracts/Dtos.cs:592-599`).
- **Auth DTOs.** Device flow, poll, and auth status responses are defined at `apps/Agentweaver.Api/Contracts/Dtos.cs:605-624`; `GitHubRepoResponse` is at `apps/Agentweaver.Api/Contracts/Dtos.cs:323-328`.
- **Team/casting DTOs.** Casting proposal, team member/team, charter/history, sync, add/rerole/update-charter shapes are at `apps/Agentweaver.Api/Contracts/Dtos.cs:630-764`.
- **Memory/decision DTOs.** `SubmitDecisionInboxRequest`, `CreateDecisionRequest`, `UpdateDecisionRequest`, `RecordMemoryRequest`, `StartSessionRequest`, and `UpdateSessionRequest` are at `apps/Agentweaver.Api/Contracts/Dtos.cs:770-823`.
- **Coordinator/backlog/board DTOs.** Start orchestration/flag/outcome/work-plan/steering contracts are at `apps/Agentweaver.Api/Contracts/Dtos.cs:830-997`; backlog/board queue DTOs are at `apps/Agentweaver.Api/Contracts/Dtos.cs:1004-1196`.
- **Model source converter.** `ModelSourceJsonConverter` preserves stable API strings `github-copilot` and `microsoft-foundry` (`apps/Agentweaver.Api/Contracts/ModelSourceJsonConverter.cs:7-23`).

## Configuration Model

`appsettings.json` defines the baseline sections:

| section/key | baseline | consumed by |
|---|---|---|
| `Logging:LogLevel` | Default `Information`, `Microsoft.AspNetCore` `Warning` | ASP.NET default logging (`apps/Agentweaver.Api/appsettings.json:2-7`) |
| `Cors:AllowedOrigins` | `[]` | CORS default policy (`apps/Agentweaver.Api/appsettings.json:8-10`; `apps/Agentweaver.Api/Program.cs:52-60`) |
| `Runs:AllowedRepositoryRoots` | `[]` | repository path validator (`apps/Agentweaver.Api/appsettings.json:11-13`; `apps/Agentweaver.Api/Security/RepositoryRootValidator.cs:30`) |
| `Sandbox:Kubernetes` | namespace/template/timeout | sandbox domain (`apps/Agentweaver.Api/appsettings.json:14-20`) |
| `Auth:GitHub:AllowedOrg`, `Scopes` | `microsoft`, `repo read:user read:org` | auth domain/org gate (`apps/Agentweaver.Api/appsettings.json:21-25`) |
| `Auth:OAuth` | signing key, issuer, audience, redirect prefixes | MCP OAuth server (`apps/Agentweaver.Api/appsettings.json:26-31`) |
| `AllowedHosts` | `*` | ASP.NET host filtering (`apps/Agentweaver.Api/appsettings.json:33`) |

Additional host keys are read directly from `IConfiguration` and may be supplied by environment variables/secrets using standard ASP.NET Core double-underscore names (for example, `Database__Path` for `Database:Path`; the app is built with `WebApplication.CreateBuilder`, `apps/Agentweaver.Api/Program.cs:31`):

- `Database:Path`: main SQLite path for `SqliteDb`; defaults to `%LOCALAPPDATA%\agentweaver\agentweaver.db` via `AppPaths.DataDirectory` (`apps/Agentweaver.Api/Infrastructure/SqliteDb.cs:15-35`; `apps/Agentweaver.Api/Infrastructure/AppPaths.cs:3-27`).
- `Database:Provider`, `Database:ConnectionString`, connection string `MemoryDb`: EF `MemoryDbContext` provider selection for `sqlite`/`sqlserver`/`azuresql`/`postgres`/`postgresql`; SQLite default uses sibling `memory.db` next to `Database:Path` or under `AppPaths.DataDirectory` (`apps/Agentweaver.Api/Program.cs:213-239`).
- `Workspace:Provider`: selects local default or `persistent-volume`/`kubernetes` provider (`apps/Agentweaver.Api/Program.cs:162-171`). `Workspace:PersistentVolume:MountRoot` is required by the persistent-volume provider (`apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:17-22`).
- `Coordinator:HeartbeatEnabled` and `Coordinator:HeartbeatIntervalSeconds`: control heartbeat status and cadence; defaults are enabled and 10 seconds (minimum 1 second) (`apps/Agentweaver.Api/Diagnostics/HeartbeatStatusStore.cs:40-56`).
- `Testing:BypassGitHubTokenAuth` and related bypass flags are only honored in Development and are production-fail-fast guarded (`apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:53-61`, `apps/Agentweaver.Api/Program.cs:33-37`).
- `Auth:ApiKey`, `Auth:User`, and `Auth:Keys` are retained for development/test bypass paths (`apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:116-130`). API.md still documents them as API-key auth (`apps/Agentweaver.Api/API.md:391-401`), but the active middleware now validates GitHub/OAuth bearer tokens on `/api/*` (`apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:48-62`).

Options classes: the host core generally reads `IConfiguration` directly instead of binding strongly typed options. The notable strongly typed config class discovered in this app tree is `KubernetesSandboxOptions`, bound from `Sandbox:Kubernetes` under `Sandbox/` (outside this core deep dive); sandbox details belong in `sandbox.md` (`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:10-25`).

## Diagnostics & Metrics

### Diagnostics

`DiagnosticsService` is singleton-safe and builds real server-side diagnostics from live stores/status surfaces; it is the shared source for REST diagnostics and MCP parity tools (`apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:11-20`). Registered and mapped at startup (`apps/Agentweaver.Api/Program.cs:190`, `apps/Agentweaver.Api/Program.cs:380`).

Emitted diagnostics:

- `GET /health`, `/api/health`, `/api/ping`, and `/healthz/workspace` return simple reachability/readiness status (`apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:16-39`).
- `GET /api/diagnostics` returns `SystemDiagnosticsDto`: API version, process start/uptime, project/run counts, active runs, generation time, total check duration, and checks (`apps/Agentweaver.Api/Diagnostics/SystemDiagnosticsDto.cs:21-32`). Checks include SQLite `SELECT 1`, data-directory write/read/delete, built-in workflow, built-in review policy, heartbeat status, project-store list, and `gh auth status` (`apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:59-104`, `apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:195-341`).
- `GET /api/projects/{id}/diagnostics` checks workspace availability, `.agentweaver/workflows`, `.agentweaver/review-policies`, active workflow, and active review policy (`apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:110-134`, `apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:348-444`).
- `GET /api/diagnostics/heartbeat` returns `HeartbeatStatusDto`: enabled flag, interval, last tick, status, last error, a ring buffer of recent tick outcomes, and an automations catalog (`apps/Agentweaver.Api/Diagnostics/HeartbeatStatusDto.cs:8-43`; assembled at `apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:140-188`). The ring buffer keeps up to 50 records and is written by the coordinator heartbeat background service (`apps/Agentweaver.Api/Diagnostics/HeartbeatStatusStore.cs:24-32`, `apps/Agentweaver.Api/Diagnostics/HeartbeatStatusStore.cs:98-122`).

### Metrics

`MetricsService` assembles dashboard and overview metrics only from live SQLite stores and the in-process heartbeat surface; it explicitly omits unavailable cost/workflow-health metrics rather than fabricating them (`apps/Agentweaver.Api/Metrics/MetricsService.cs:10-19`; `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:5-9`). It is registered and mapped at startup (`apps/Agentweaver.Api/Program.cs:191`, `apps/Agentweaver.Api/Program.cs:381`).

Emitted metrics:

- `GET /api/projects/{id}/dashboard` returns a `ProjectDashboardDto` with summary counters, 30-day throughput, and an agent leaderboard (`apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:19-38`; `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:15-73`). Queries aggregate runs by project, statuses, dates, and agent names (`apps/Agentweaver.Api/Metrics/MetricsService.cs:55-79`, `apps/Agentweaver.Api/Metrics/MetricsService.cs:81-212`).
- `GET /api/overview` returns `OverviewDto`: generated time, at-a-glance counters, live sessions, active workflow runs, active projects, and recent activity (`apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:40-48`; `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:79-152`). It computes health as degraded if heartbeat is disabled/errored or any run is `merge_failed` (`apps/Agentweaver.Api/Metrics/MetricsService.cs:327-367`).

## Extension Points & Gotchas

- **Endpoint extension methods are the routing seam.** `Program.cs` maps each domain through an extension method (`apps/Agentweaver.Api/Program.cs:365-382`). Add new host/core endpoints as extension methods and register them explicitly in this list.
- **Custom middleware does not rely on endpoint metadata.** `AllowAnonymous()` is used on `/api/server/info` (`apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:199-204`), but `GitHubTokenAuthMiddleware` only exempts exact paths/non-API routes, not endpoint metadata (`apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:134-143`). Treat new `/api/*` endpoints as protected unless the middleware exemption list is updated.
- **`/` is mapped but not exempt from org auth.** The root route is non-API, so token auth passes it, but it is not in the org middleware exempt prefixes (`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:31`; `apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationMiddleware.cs:25-39`). Unauthenticated callers should expect a 401 rather than the banner.
- **OAuth rate limiting is opt-in per endpoint.** `UseRateLimiter()` is global, but only OAuth flow endpoints call `RequireRateLimiting` (`apps/Agentweaver.Api/Program.cs:139-155`; `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:128`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:243`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:306`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:338`).
- **SSE has live and durable layers.** `RunStreamStore` retains up to 256 completed in-memory histories (`apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:159-200`), while `SqliteRunEventStream` persists run events to `memory.db` before channel fan-out and supports replay-then-tail (`apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs:12-30`, `apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs:79-151`).
- **API.md documents durable run events.** API.md now describes SSE replay through `IRunEventStream` and the persisted `/api/runs/{id}/events` endpoint (`apps/Agentweaver.Api/API.md:100-184`, `apps/Agentweaver.Api/API.md:344-352`; implementation at `apps/Agentweaver.Api/Program.cs:72`; `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:502-545`).
- **Database split matters.** Main operational tables are in `agentweaver.db` via `SqliteDb`; EF memory/run-events data is in `memory.db` by default (`apps/Agentweaver.Api/Infrastructure/SqliteDb.cs:15-35`; `apps/Agentweaver.Api/Program.cs:213-239`; `apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs:61-75`).
- **Workspace provider is config-selected.** `Workspace:Provider=local` uses caller-supplied filesystem paths; `persistent-volume`/`kubernetes` ignores requested path and deterministically maps project id under the mount root (`apps/Agentweaver.Api/Infrastructure/LocalFilesystemWorkspaceProvider.cs:5-53`; `apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:6-36`).
- **Persistent-volume health uses write probes, not `Directory.Exists`.** This avoids CIFS/statx false negatives; `/healthz/workspace` depends on `IsMountRootHealthy()` (`apps/Agentweaver.Api/Infrastructure/PersistentVolumeWorkspaceProvider.cs:77-120`; `apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:28-39`).
- **Sandbox policy writes are GitOps-style and project-scoped.** `YamlSandboxPolicyStore` reads/writes `.agentweaver/settings.yml`, preserves non-sandbox sections, returns defaults for absent/malformed YAML, and only writes policies for known project workspaces (`apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs:7-13`, `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs:36-84`, `apps/Agentweaver.Api/Infrastructure/YamlSandboxPolicyStore.cs:86-100`).
- **Path validation is centralized for file content/diff routes.** `EndpointHelpers.TryValidateRelativePath` rejects null/control chars, rooted/UNC/device/drive-relative paths, parent traversal, and Windows ADS (`apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:156-200`). The run file handler also documents a known catch-all `/content` suffix edge case for files literally named `content` in a subdirectory (`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1783-1789`).
- **Checkpoint startup is defensive.** `ResilientCheckpointStore` sanitizes/quarantines corrupt MAF checkpoint indexes so the API can start instead of crash-looping (`apps/Agentweaver.Api/Infrastructure/ResilientCheckpointStore.cs:7-43`).
- **No temp directory for app data.** `AppPaths` resolves `%LOCALAPPDATA%\agentweaver` or app base directory and explicitly documents that system temp is never used (`apps/Agentweaver.Api/Infrastructure/AppPaths.cs:3-27`).
