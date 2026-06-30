# Squad Decisions

## 2026-06-27: Key Vault Token Store Deployment

**Date:** 2026-06-27T23:12:47-07:00  
**Author:** Link (infra/k8s)  
**Status:** DEPLOYED

**Decision:** KV token store deployed to AKS as image tag acd via merge of sabbour/mcp-oauth into sabbour/spec-018. GitHub access and refresh tokens now persist in gentweaver-kv across redeploys.

**Rationale:** OAuth tokens were previously ephemeral in-memory; Key Vault integration enables token recovery after pod restarts, eliminating reauthentication on every deployment.

---

## 2026-06-27: MCP OAuth Token System (Distinct from KV Store)

**Date:** 2026-06-27T23:12:47-07:00  
**Author:** Seraph (auth/integration)  
**Status:** IN_PROGRESS

**Decision:** MCP OAuth is a separate token system (RS256 JWTs minted by the API, signing key from KV secret mcp-oauth-signing-key) — distinct from the GitHub token KV store. Do not conflate them when debugging reauth issues.

**Rationale:** Two independent token flows:
1. **GitHub KV store:** OAuth tokens exchanged with GitHub, stored in Key Vault, used by API to call GitHub APIs.
2. **MCP OAuth:** RS256 tokens minted by the API (signed with KV secret), sent to MCP for authentication between MCP and API.

Local MCP auth failures (omitted Auth__Mcp__Issuer/Audience/JwksUri) cause JWKS validation to fail. AKS MCP auth failures may involve iss/aud configuration mismatch or JWKS fetch reachability.

---

## 2026-06-27: GitHub PAT (Personal Access Token) Rotation + Management UI

**Date:** 2026-06-27T22:59:18-07:00  
**Author:** Link (infra/k8s)  
**Status:** BACKLOG

**Decision:** GitHub PAT rotation will be a managed workflow (manual trigger via UI, automatic re-link after rotation). Not auto-rotated; users explicitly rotate via the management panel.

**Rationale:** GitHub PATs have expiration times set at creation; they don't auto-renew. The integration allows re-linking at any time (exchanging a new PAT for old). The management UI will display token expiry (if set) and allow users to rotate before expiry. Seraph: implement the re-link endpoint and management UI for token rotation.

---

## 2026-06-27: .squad/ untracked (git-ignored for real)

**Date:** 2026-06-27T21:13:54-07:00  
**Author:** Link (repo-mgmt)  
**Status:** IMPLEMENTED

**Decision:** .squad/ is gitignored and untracked (143 files removed from index, new .gitignore entry). Final pre-push step will scrub any residual .squad/ commits from history.

**Rationale:** Squad state is team-specific and not suitable for public repos. Setting is permanent going forward.

---

## 2026-06-28: Browser-preview capability advertised to live-path agents

**Date:** 2026-06-28T10:05:28-07:00  
**Author:** Morpheus (sandbox/preview)  
**Status:** IMPLEMENTED

**Decision:** Browser-preview capability advertised to live-path agents (config-gated); MCP server confirmed NOT wired into spawned agents (EnableConfigDiscovery=false) so not advertised.

**Rationale:** Added agent-facing capability advertisement for the browser-preview feature (commit 40ddb846, branch feat/sandbox-preview-proxy). Injection point: apps/Agentweaver.Api/Runs/RunOrchestrator.cs BuildContextAsync → new AppendCapabilities() applied over AppendMemoryProtocol(). BROWSER PREVIEW block is config-gated on Sandbox:Preview:Enabled (default false → unchanged prompt). MCP FINDING: the standalone agentweaver MCP server (apps/Agentweaver.Mcp) is NOT reachable by spawned in-run agents and was therefore NOT advertised. Spawned agents instead get explicit native loopback function tools (AgentweaverApiTools: list_decisions, get_memory, list_inbox, submit_decision, record_memory). Tests: RunOrchestratorCapabilitiesTests (5) green.

---

## 2026-06-28: Browser preview implemented as Gateway-direct reverse proxy

**Date:** 2026-06-28T09:57:40-07:00  
**Author:** Morpheus (sandbox/preview)  
**Status:** IMPLEMENTED

**Decision:** Browser preview implemented as Gateway-direct (HTTPRoute→ClusterIP Service→pod), annotation-driven reaper, ships dark behind Sandbox:Preview:Enabled.

**Rationale:** Architecture: Gateway(preview) → per-preview HTTPRoute → per-run ClusterIP Service → run's sandbox pod. NO API-loopback proxy. All per-preview K8s objects are created/deleted by the API at RUNTIME via the in-cluster KubernetesClient. Replica-safe by design: ALL per-preview state lives in HTTPRoute annotations (preview-expires-at, preview-max-until, preview-run, preview-token, preview-owner), never in process memory. Capability token = 3 words from a curated 64-word wordlist + 4 hex (e.g. swift-falcon-amber-7a3f), CSPRNG-drawn, ~2^34 entropy. keep_after_run=TRUE: run-end and pod-release-on-suspend do NOT delete the preview; only the reaper (idle/max expiry) or the EXPLICIT user DELETE endpoint removes it. Ships DARK: when Sandbox:Preview:Enabled=false (default) ISandboxPreviewService.Enabled=false, the reaper idles, and the POST /port-forward endpoint keeps the legacy kubectl path verbatim. Zero behavior change by default. Frontend contract: POST /api/runs/{runId}/sandbox/port-forward returns session_id (=token), local_port:0, target_port, pod_name, started_at, preview_url. Build: dotnet build 0 errors. Tests: 32 new preview unit tests green; 232 Sandbox-filtered tests green.

---

## 2026-06-28: Spawned agents use native loopback tools, NOT MCP

**Date:** 2026-06-28T10:18:09-07:00  
**Author:** Squad-Coordinator  
**Status:** DECIDED

**Decision:** Spawned agents use native loopback tools, NOT MCP — no MCP-into-sandbox wiring (user decision).

**Rationale:** @sabbour decision: do NOT wire MCP into spawned sandbox agents. Native loopback function tools (AgentweaverApiTools: list_decisions, get_memory, list_inbox, submit_decision, record_memory — referenced in AgentBasePrompt.Base + WorkerMemoryProtocol) are sufficient. The hermetic sandbox deliberately sets EnableConfigDiscovery=false and registers no mcpServers; standalone apps/Agentweaver.Mcp remains for EXTERNAL clients only. No MCP-into-sandbox follow-up will be scoped. User input: "1, no. native tools is fine here."

---

## 2026-06-28: AKS App Routing single-label wildcard certificate constraint

**Date:** 2026-06-28T10:11:28-07:00  
**Author:** Squad-Coordinator  
**Status:** SPIKE_COMPLETE

**Decision:** AKS App Routing issues only single-label *.{zone} wildcard certs (no nested) — preview hosts use {token}.{zone}.

**Rationale:** SPIKE RESULT: AKS App Routing managed-domain `DefaultDomainCertificate` does NOT support nested wildcard certs. The DDC CRD has no spec.hostname field; the controller always issues `*.{zone}` (single-label wildcard) regardless of object/secret name. DECISION: preview hosts use SINGLE-LABEL `{capability-token}.{zone}` reusing the existing Secret/agentweaver-tls wildcard cert. ZoneSuffix=6a3de4fe60529400010f3fba.westus2.staging.aksapp.io. Runtime cert probe removed from 30-deploy.sh. Committed 2f1e1f6 on feat/sandbox-preview-proxy. OPEN: (a) two-gateway wildcard-vs-exact-host overlap on the app-routing Istio bundle is low-risk but unconfirmed — smoke-test on first deploy; (b) NetworkPolicy preview ingress hardcodes TCP 3000 — must align with Morpheus's actual preview target port model.

---

## 2026-06-28: Repository going public + MIT license + agent definition auto-generation

**Date:** 2026-06-28T16:04:17-07:00  
**Author:** Link (repo-mgmt)  
**Status:** IMPLEMENTED

**Decision:** Repository going public on GitHub with MIT license. Agent definition auto-generation wired into build.

**Rationale:** Repository published at github.com/microsoft/agentweaver. .gitignore now allows docs/ *.md but blocks .squad/ (squad state is team-specific). BuildSpec.BeforePublish hooks agent definition auto-generation into the MSBuild pipeline (agent definitions published to docs/agents/).

---

## 2026-06-28: Browser preview shipped to main with replica-safe pod resolution

**Date:** 2026-06-28T16:04:17-07:00  
**Author:** Morpheus  
**Status:** IMPLEMENTED

**Decision:** Browser preview shipped to main with replica-safe pod resolution and annotation-driven HTTPRoute state.

**Rationale:** Pod name resolved at runtime from PodNameRegistry (populated during AgentHost pod launch). HTTPRoute carries replica-safe state in annotations; no process-memory singleton required. Architecture supports multi-replica API deployments without coordination overhead.

---

## 2026-06-28: Sandbox aligns to agent-sandbox v0.5.0 native v1beta1 warmPoolRef

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (runtime)  
**Status:** IMPLEMENTED

**Decision:** Sandbox resources aligned to agent-sandbox v0.5.0 native spec with v1beta1 warmPoolRef in SandboxTemplate/SandboxClaim.

**Rationale:** Dropped Tekton Task dependency; now using Kubernetes-native SandboxWarmPool, SandboxTemplate, and SandboxClaim v1beta1. Schema changes: SandboxWarmPool.spec has image instead of imageRef, metadata.name is now writable (was read-only). Validation passed; tests updated; AgentHost dependency locked to v0.5.0.

---

## 2026-06-28: agent-host SandboxWarmPool runs at replicas:0

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (runtime)  
**Status:** IMPLEMENTED

**Decision:** agent-host SandboxWarmPool configured with replicas:0 (no pre-warmed pods).

**Rationale:** Pre-warmed pod pool was overhead without correctness benefit. On-demand pod launch (replicas:0 → scale to 1 on claim) reduces resource waste. Claim release scales pool back to 0. Tradeoff: slightly slower first turn; benefit: pod-per-run isolation + faster cleanup.

---

## 2026-06-28: ResilientCheckpointStore uses per-pod fallback directory under replicas:2

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (runtime)  
**Status:** IMPLEMENTED

**Decision:** ResilientCheckpointStore configured with per-pod fallback directory (replicas:2 for HA) only for dev/demo; production uses Postgres.

**Rationale:** Fallback checkpoint directory allows local storage when Postgres is unavailable (e.g., during local dev setup). Production always targets Postgres; file fallback is a safety net for non-production only. Per-pod directory layout supports 2-replica HA (each pod has isolated checkpoint dir).

---

## 2026-06-28: Deployed 291a6bf to agentweaver-aks-2; sandbox browser-preview shipped ENABLED; Phase B smoke GREEN

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Link (infra)  
**Status:** DEPLOYED

**Decision:** Commit 291a6bf deployed to agentweaver-aks-2 with Sandbox:Preview:Enabled=true. Phase B smoke tests passed.

**Rationale:** Phase B operationally tests browser-preview at scale (multi-replica, Karpenter scaling, pod churn). Smoke passed: preview URLs functional, pod cleanup on run-end works, no resource leaks, multi-replica requests balanced.

---

## 2026-06-28: Copilot-auth blocker pauses autonomous preview demo; custom Agentweaver GitHub App cannot mint Copilot-entitled tokens

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Squad-Coordinator  
**Status:** BLOCKED

**Decision:** Autonomous preview demo is paused; cannot proceed until Copilot GitHub App token minting is resolved.

**Rationale:** Custom Agentweaver GitHub App cannot mint tokens with Copilot entitlements (oauth scope copilot_api). GitHub API returns 403 FORBIDDEN when the app attempts to request copilot_api scope for any user. BLOCKER: app does not have copilot_api permission grant from GitHub. Workaround: use personal access tokens from Copilot-entitled GitHub accounts. Investigation: does GitHub require a GitHub-approved security audit or special enterprise plan? Coordinator to liaise with GitHub account team.

---

## 2026-06-28: Agent-initiated preview surfaces use `start_preview` with tool approval, not MAF RequestPort

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (sandbox/preview)  
**Status:** IMPLEMENTED

**Decision:** Agent-initiated preview surfaces use explicit `start_preview` tool (via tool_use approval), not MAF RequestPort function call.

**Rationale:** Explicit tool approval model (MCP-style tool_use) is cleaner than RequestPort side-effect. Agents call start_preview; users approve in MCP request; API launches the preview. RequestPort remains available for agent-native HTTP forwarding (deprecated, kept for backward compat).

---

## 2026-06-28: A2A cold-starts wait for AgentHost `/healthz` before first turn

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (runtime)  
**Status:** IMPLEMENTED

**Decision:** Agent-to-Agent (A2A) remote agent cold-starts now wait for AgentHost `/healthz` readiness probe before executing the first turn.

**Rationale:** Prevents turn timeouts due to AgentHost pod startup delays. Readiness check is retried with exponential backoff; timeout is configurable. Remote agent proxy applies the wait at proxy construction time, not per-turn.

---

## 2026-06-28: Production checkpoints move to Postgres; file checkpoint fallback is dev/safety only

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (runtime)  
**Status:** IMPLEMENTED

**Decision:** Production checkpoints now persist to Postgres; file-based checkpoints are dev/safety fallback only.

**Rationale:** Postgres provides durable, replicated checkpoint storage. File fallback is a safety net when Postgres is unavailable (e.g., local dev, pod crash before Postgres init). ResilientCheckpointStore tries Postgres first, falls back to file on connection failure.

---

## 2026-06-28: AgentHost receives submitting user ID for user-scoped credential lookup

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (runtime)  
**Status:** IMPLEMENTED

**Decision:** AgentHost receives submitting user ID in the A2A turn request, enabling user-scoped credential lookup (GitHub token, shared secrets).

**Rationale:** Multi-user sandbox support: run-scoped tokens must be associated with the submitting user for credential isolation. User ID passed in turn request header or body; AgentHost uses it to hydrate GitHubTokenScope and load user-scoped secrets.

---

## 2026-06-28: Kata pod sandboxing cannot be provisioned by AKS NAP/Karpenter today

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Link (infra)  
**Status:** OPEN

**Decision:** Kata pod sandboxing is not achievable via AKS NAP or Karpenter today.

**Rationale:** SPIKE RESULT: AKS Karpenter does not support kata RuntimeClass. NAP (Node Auto Provisioning) also does not support kata. Kata requires a host kernel configured with kata support (not standard on AKS node images). WORKAROUND: Pod-level isolation via NetworkPolicy + PodSecurityPolicy remains the default. FUTURE: Watch for AKS Kata support in future AKS releases or consider custom node image builds.

---

## 2026-06-28T16:05:00-07:00 — Web session exchange codes are DB-backed and replica-safe

**Date:** 2026-06-28T16:05:00-07:00  
**Author:** Morpheus (auth)  
**Status:** IMPLEMENTED

**Decision:** Web session exchange codes (OAuth callback flow) are DB-backed and replica-safe.

**Rationale:** Multi-replica API deployments require durable exchange code storage. Codes are short-lived (5 min TTL) and stored in a replicated data store. Callback redirect validates code from DB, extracts state, and redirects to the agent's web session.

---

## 2026-06-29: Kanban board — Active column after Ready, Problems in own section

**Date:** 2026-06-29T10:00:00-07:00  
**Author:** Squad-Coordinator  
**Status:** IMPLEMENTED

**Decision:** Kanban board workflow now has Active column (between Ready and Done) and Problems section.

**Rationale:** Active column provides visibility into work-in-progress (WIP) tasks. Problems section segregates blocked/unresolved issues from the backlog.

---

## 2026-06-29: Removed static MCP API key; MCP auth uses OAuth only

**Date:** 2026-06-29T10:00:00-07:00  
**Author:** Link (auth)  
**Status:** IMPLEMENTED

**Decision:** Static MCP API key removed; MCP authentication now uses OAuth tokens only.

**Rationale:** Single authentication model (OAuth) reduces surface area. Eliminates shared API key which is harder to rotate. All MCP connections use RS256 JWT issued by the API.

---

## 2026-06-29: AKS cluster switches from NAP to cluster-autoscaler with dedicated katapool

**Date:** 2026-06-29T10:00:00-07:00  
**Author:** Link (infra/k8s)  
**Status:** DEPLOYED

**Decision:** AKS cluster switched from Node Auto Provisioning to cluster-autoscaler with a dedicated katapool node pool.

**Rationale:** Karpenter (NAP's successor) does not support Kata runtime. Fallback to cluster-autoscaler for general workload scaling; dedicated katapool reserved for future Kata pod sandboxing when AKS support arrives.

---

## 2026-06-29: System pool gets CriticalAddonsOnly taint; dedicated apppool for app workloads

**Date:** 2026-06-29T10:00:00-07:00  
**Author:** Link (infra/k8s)  
**Status:** DEPLOYED

**Decision:** System pool tainted with CriticalAddonsOnly; new dedicated apppool added for app workloads.

**Rationale:** Isolates critical cluster services (DNS, metrics, logs) from user workloads. Apppool scales independently. Better resource utilization and fault isolation.

---

## 2026-06-30: Security fix: per-pod CSI SPC for AgentHost token isolation + dev secrets review

**Date:** 2026-06-30T00-22-27Z  
**Author:** Link  
**What:** Security fix: per-pod CSI SPC for AgentHost token isolation + dev secrets review  
**References:** security-audit-2026-06-29  
**Implementation:** Creates run-scoped SecretProviderClass containing only ghtok-user--{base32(userId)}, clones AgentHost SandboxTemplate to point at that SPC, creates run-scoped SandboxWarmPool, cleans up on release or failed launch. Centralizes dynamic AgentHost resource naming. Reaps run-scoped SandboxWarmPool, SandboxTemplate, and SecretProviderClass when deleting orphaned AgentHost claims. Removes obsolete shared-SPC patch service. Removed AgentHostUserTokenSyncService DI/use from GitHubOAuthRedirectService and Program.cs. Documents static agentweaver-user-tokens as installation-only/base parameters. Updated token-delivery docs/comments from shared SPC patching to per-run SPCs. Grants API create/delete for per-run SandboxTemplates, SandboxWarmPools, and SecretProviderClasses. Added UserSecretsId and documented dotnet user-secrets for Auth:GitHub:ClientSecret. Updated test coverage for run-scoped SPC/template/pool behavior and no-user launch failure. Dev secret findings: appsettings.Development.json contains real-looking 40-character ClientSecret but is gitignored and not tracked; no local dev Key Vault/App Configuration delivery (production has Key Vault/CSI). Added .NET user-secrets support and docs.

---

## 2026-06-30: Security fix: per-user GitHub token scoping and disabled PVC token mirror

**Date:** 2026-06-30T00-13-47Z  
**Author:** Morpheus  
**What:** Security fix: per-user GitHub token scoping and disabled PVC token mirror  
**References:** security-audit-2026-06-29  
**Implementation:** KeyVaultGitHubTokenStore now uses diskMirror: null while retaining diskFallback for lazy migration; IGitHubTokenScopeProvider is now config-driven with safe default CallerTokenScopeProvider and explicit installation opt-in. OAuth callback now writes only to GitHubTokenScope.ForUser(login) and throws InvalidOperationException when login is missing/unknown. Removed shared-directory user discovery behavior; missing user id now logs a warning and falls back to installation scope. Wires ILogger<SharedUserScopeProvider> into SharedUserScopeProvider registrations so fallback warning is emitted.

---

## 2026-06-30: Security: per-run bearer token on AgentHost A2A turn endpoint

**Date:** 2026-06-30T01-13-05Z  
**Author:** Morpheus  
**What:** Security: per-run bearer token on AgentHost A2A turn endpoint  
**References:** security-audit-2026-06-29, a2a-bearer-token-phase1  
**Implementation:** AgentHost:TurnBearerToken option protects A2A turn submissions. Bearer-auth middleware requires Authorization: Bearer {TurnBearerToken} for POST {A2APath}/v1/message:stream. Runtime-visible per-run token registry contract added. Per-run in-memory registry extended to store and clear AgentHost turn bearer tokens. Generates 256-bit random token per AgentHost pod launch, injects AgentHost__TurnBearerToken into SandboxClaim env, registers token by run ID, clears on failure/release. Passes turn-token registry into KubernetesSandboxExecutor. Registers IAgentHostTurnTokenRegistry using PodNameRegistry singleton. Injects token registry into RemoteAgentProxy instances. RemoteAgentProxy applies registered run token as default Authorization bearer header on A2A HttpClient. Updated factory/proxy DI tests for optional token-registry dependency.

---

## 2026-06-30: AIC capture via AssistantUsageEvent (Feature 019)

**Date:** 2026-06-30T00-53-45Z  
**Author:** Morpheus  
**What:** AIC capture via AssistantUsageEvent (Feature 019)  
**References:** Feature 019 - AI Credit and Token Usage Monitoring, packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs, packages/Agentweaver.Domain/EventTypes.cs  
**Implementation:** Token and AIC capture implemented by detecting AssistantUsageEvent.RawRepresentation in the existing StreamTurnOnceAsync chunk loop. Accumulators reset per SetupAsync call. agent.turn.usage event emitted at end of each ExecuteStreamingLoopAsync. AssistantUsageData.CopilotUsage.TotalNanoAiu is the authoritative AIC signal from the GitHub Copilot SDK. Per-turn accumulation avoids double-counting on retry loops. TotalNanoAiu is a double (not long as documented); explicit cast to long applied at accumulation time.

---

## 2026-06-30: Token usage backend stack (Feature 019)

**Date:** 2026-06-30T00:00:00Z  
**Author:** Tank  
**What:** Token usage backend stack (Feature 019)  
**References:** Feature 019 - AI Credit and Token Usage Monitoring  
**Status:** IMPLEMENTED (build: 0 errors)  
**Implementation:** Complete backend implementation: token_usage_records table, dual-backend store (SQLite + EF), background projection service from event stream, four-level hierarchy API endpoints (org/project/run/turn), metrics extension, MCP tools. Captures real AIC and token data from agent.turn.usage run events emitted by Morpheus's runtime changes. All data served from persistent store; no aggregation in clients.

---

## 2026-06-30: Security: MCP route parameter escaping + remove hardcoded admin bypass

**Date:** 2026-06-30T01-12-21Z  
**Author:** Tank  
**What:** Security: MCP route parameter escaping + remove hardcoded admin bypass  
**References:** security-audit-2026-06-29, mcp-path-traversal, admin-bypass  
**Status:** DEPLOYED (commit 5373893)  
**Implementation:** 86 MCP tool API paths now URI-escaped for route parameters (project_id, task_id, run_id, etc.). Hardcoded admin bypass removed from all 4 endpoint files. Validation: no remaining caller.User admin comparisons found; all builds pass. MCP path traversal vulnerability closed by escaping project_id, run_id, task_id, entry_id, decision_id, agent_name, memory_id in all backlog, coordinator, memory, project, run, team, workflow, and workspace tools. Admin bypass was a security liability; removed entirely from ProjectEndpoints, TeamEndpoints, RunEndpoints, BacklogEndpoints. All MCP Tools files: URI-escaped route parameters. All 4 endpoint files: removed hardcoded admin bypass.

---

## 2026-06-30: Token usage frontend (Feature 019)

**Date:** 2026-06-29T18-15-00-07:00  
**Author:** Trinity  
**What:** Token usage frontend (Feature 019)  
**Status:** IMPLEMENTED (all builds pass)  
**Implementation:** Frontend surfaces AIC and token data via TokenUsagePanel component, live counter on WatchPage, time-range section on DashboardPage, app-level section on OverviewPage (admin-gated, degrades on 403). Display logic is pure presentation with no aggregation in UI. Backend API provides authoritative data. Frontend simply renders hierarchical breakdowns by org/project/run/turn for operator visibility into usage patterns and cost allocation. All UI tests pass; Feature 019 frontend components green.

---
