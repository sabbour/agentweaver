# Squad Decisions Archive

Archived by Scribe at 2026-07-08T00:57:28-07:00. Policy: decisions.md was 237700 bytes, so entries older than 7 days were archived. Cutoff kept in active file: 2026-07-01 and newer.

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
