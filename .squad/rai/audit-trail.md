# RAI Audit Trail

> Append-only evidence log. Entries are redacted — never contains raw secrets or harmful content.

<!-- Rai appends findings below -->

## 2026-06-27T00:49:00-07:00 — Targeted Review: GitHubOrgAuthorizationService.cs (branch: sabbour/mcp-oauth)

**Reviewer:** Rai | **Requested by:** sabbour | **Verdict:** 🟢 Green

**Change summary:** (A) `public_members` probe now sends user Bearer token to use the 5000/hr authenticated rate-limit bucket; (B) GitHub rate-limit responses (429 / 403+`X-RateLimit-Remaining:0` / `Retry-After`) map to `Inconclusive` (not cached) instead of a definitive denial.

**Credential/secret exposure:** All new `LogWarning` calls log only `login`, `_allowedOrg`, and `StatusCode` integer. The `accessToken` is passed only as an HTTP `Authorization: Bearer` header (line 175) and appears nowhere in any log statement. ✅

**Injection / SSRF:** All three URL constructions hardcode the host (`api.github.com`). User-controlled `login` and config-sourced `_allowedOrg`, `_teamOrg`, `_teamSlug` are all wrapped in `Uri.EscapeDataString`. No host injection or path traversal vector exists. ✅

**Security-control weakening / fail-open:** `Inconclusive` is handled fail-closed at every call site:
- Middleware (`GitHubOrgAuthorizationMiddleware` line 170–179): → HTTP 403 "Please retry." ✅
- Token issuance (`McpOAuthBrokerService` line 196): `!= OrgAuthResult.Allowed` → Denied. ✅
- Token refresh (`OAuthServerEndpoints` line 211–217): intentional soft fallback to issuance-time org claim — explicitly documented for transient rate-limit conditions on an internal tool. This is a deliberate design tradeoff, not a bug. ✅

**Rate-limit boolean precedence:** `&&`/`||` binding in `isRateLimited` correctly scopes the `Forbidden` condition to both sub-checks. ✅

**No critical issues found. Work may proceed.**

---

## 2026-06-26T10:57:00-07:00 — Re-Review: MCP OAuth 2.1 F1-F4 Fixes (commit eb6f8f6, `sabbour/mcp-oauth`)

**Scope**: `Security/ApiKeyAuthMiddleware.cs` (GitHubTokenAuthMiddleware), `Security/TestingBypassGuard.cs` (new),
`Auth/GitHubOrgAuthorizationMiddleware.cs`, `Auth/OAuth/McpOAuthBrokerService.cs` (OAuthServerConfig),
`Endpoints/OAuthServerEndpoints.cs`, `Program.cs`, `appsettings.json`
**Verdict**: 🟢 GREEN — 🔴 RED CLEARED. Branch may proceed toward merge.
**Reviewer**: Rai | **Requested by**: Ahmed Sabbour (@sabbour)

### F1 — Auth Bypass Guard: RESOLVED ✅

Two independent barriers now prevent any bypass activation in Production:

**Barrier 1 — Middleware-level**: `_bypassForTests = environment.IsDevelopment() && bypassConfigured`
applied in both `GitHubTokenAuthMiddleware` and `GitHubOrgAuthorizationMiddleware`. `IsDevelopment()`
returns `true` only for the literal "Development" environment; all other names (`Production`, `Staging`,
custom) yield `false`. `LogCritical` fires both when bypass is active AND when it is configured-but-ignored.

**Barrier 2 — Startup-level**: `TestingBypassGuard.EnsureNotEnabledInProduction` is the very first call
in `Program.cs` before any service registration. Under `IsProduction()`, if either bypass flag is `true`,
an `InvalidOperationException` is thrown — the process hard-fails before serving any request.

Attack paths confirmed closed:
- Production env + env var bypass → startup throw ✓
- Staging env + env var bypass → `IsDevelopment()=false`, bypass silently ignored ✓
- Development env + flag set → bypass active with LogCritical (intended for tests) ✓

### F2 — Redirect URI Allowlist: RESOLVED ✅ (minor residual noted)

`IsAllowedRedirectUri` now rejects `userinfo` (`user@host`) before any scheme check; loopback HTTP
remains permitted; HTTPS requires a match against `Auth:OAuth:AllowedRedirectUriPrefixes` (empty by
default → all HTTPS rejected until operator configures the list); comparison is `StringComparison.Ordinal`.

Residual advisory: prefix matching (`StartsWith`) does not anchor at domain boundary —
`https://app.example.com` prefix also matches `https://app.example.com.evil.com/`. Operators should
configure prefixes with a trailing `/`. T5 DCR (exact-match per-client) eliminates this fully.

### F3 — Rate Limiter: RESOLVED ✅

Fixed-window 20 req/min per remote IP; `QueueLimit=0`; 429 on rejection. `app.UseRateLimiter()` in
pipeline before auth middleware. `.RequireRateLimiting("oauth")` on `/oauth/authorize` and
`/oauth/token`. Discovery endpoints intentionally excluded. ✓

### F4 — Refresh Token Placeholder: RESOLVED ✅

`refresh_token` removed from token response and from `grant_types_supported` in AS metadata. ✓

### F5 — Pre-existing session_token in URL: Advisory (out of scope, still open)

Not addressed in eb6f8f6 (correct). Remains tracked 🟡 for post-T3 cleanup.

---

## 2026-06-26T10:15:00-07:00 — Security Review: MCP OAuth 2.1 T1-T3 (commit 358bdcab, `sabbour/mcp-oauth`)

**Scope**: `Auth/OAuth/McpOAuthBrokerService.cs`, `Auth/OAuth/McpTokenService.cs`,
`Endpoints/OAuthServerEndpoints.cs`, `Endpoints/AuthEndpoints.cs`,
`Security/ApiKeyAuthMiddleware.cs`, `Program.cs`, `appsettings.json`
**Verdict**: 🔴 RED — RELEASE BLOCKED
**Reviewer**: Rai | **Requested by**: Ahmed Sabbour (@sabbour)

### Credential / Secret Logging Checklist (All Clear ✓)

| Check | Result |
|-------|--------|
| Signing key (PEM) logged? | No — only "loaded from config" info message. |
| GitHub `client_secret` logged? | No — broker never touches the secret directly. |
| GitHub user access token logged? | No — used for org-check only; not logged. |
| Authorization code logged? | No — only `{Login}` appears in log lines. |
| `Authorization:` header logged? | No — SHA-256 hash used as cache key only. |
| JWT access token logged? | No — returned in response body under TLS only. |

### Findings

**🔴 F1 — Auth Bypass Enableable in Production (`Testing:BypassGitHubTokenAuth`)**
- File: `Security/ApiKeyAuthMiddleware.cs` (class `GitHubTokenAuthMiddleware`)
- `configuration.GetValue<bool>("Testing:BypassGitHubTokenAuth")` is read with no
  `IHostEnvironment.IsDevelopment()` guard. The flag is activatable in production via a
  Kubernetes env-var (`Testing__BypassGitHubTokenAuth=true`). When active, all GitHub token
  validation on `/api/*` is bypassed — any bearer string is accepted.
- Fix: gate on `environment.IsDevelopment()` in constructor; promote log to `LogCritical`.
- Fix agent: Forge (Tank locked out — Reviewer Rejection Protocol active).

**🟡 F2 — Open Redirect: Any HTTPS URI Accepted as `redirect_uri`**
- File: `Auth/OAuth/McpOAuthBrokerService.cs` (`OAuthServerConfig.IsAllowedRedirectUri`)
- No per-client allowlist until T5 DCR ships; any `https://` URI is accepted. PKCE prevents
  token theft but user browser is redirected to untrusted origin.
- Fix: add static config-backed `Auth:OAuth:AllowedRedirectUriPrefixes` as interim guard.

**🟡 F3 — No Rate Limiting on Public OAuth Endpoints**
- Files: `Endpoints/OAuthServerEndpoints.cs`
- `/oauth/authorize` and `/oauth/token` are `AllowAnonymous` with no `AddRateLimiter` policy.
- Fix: apply fixed-window rate limit (e.g., 20 req/min per IP) to `/oauth/*` routes.

**🟡 F4 — Refresh Token Placeholder Silently Non-Functional**
- File: `Endpoints/OAuthServerEndpoints.cs` (token endpoint)
- `refresh_token` returned but never persisted (T4 TODO). Conformant clients will fail silently.
- Fix: omit `refresh_token` from response and remove from `grant_types_supported` until T4.

**🟡 F5 — GitHub Token in Redirect Query Parameter (pre-existing, visible in diff)**
- File: `Endpoints/AuthEndpoints.cs` line 93 (pre-existing web sign-in path)
- GitHub OAuth `accessToken` placed in `?session_token=<token>` URL — leaks to logs/history.
- Fix: replace with server-side one-time code exchange; tracked for post-T3 cleanup.

## 2026-06-09T17:23:44-07:00 — Pre-Implementation Security Review: Per-Run Workflow Construction (seraph-perrun-workflow-bugfix)

**Scope**: RunWorkflowFactory.cs — replace cached singleton `Workflow` with fresh instance per `StartAsync`/`ResumeAsync`.

**Verdict**: 🟢 PASS

| Area | Finding | Rating |
|------|---------|--------|
| Cross-run state leakage | Old design stored merge-data in workflow state scope (`MergeDataScope`). Under the bug, 2nd run threw before execution, so no practical leak occurred. Per-run Workflow **eliminates** this theoretical vector entirely — each run gets isolated state scope. No new leakage path introduced. | PASS |
| Concurrency / shared singletons | IAgentRunner, IWorktreeOperations, IMergeCoordinator, RunStreamStore remain shared singletons. All are stateless or key their mutable state by runId (ConcurrentDictionary, per-repo lock, per-runId stream entry with owner guard). Executors are freshly constructed per BuildWorkflow() call — no shared mutable executor state. | PASS |
| Checkpoint store isolation | FileSystemJsonCheckpointStore writes to `{checkpointDir}/{runId}/`. Shared `_checkpointManager` dispatches by sessionId=runId. Per-run Workflow does not change path scoping. One run cannot read/resume another's checkpoint without knowing the target runId (runIds are GUID). | PASS |
| DoS / resource exhaustion | BuildWorkflow() allocates lightweight executor objects and a graph structure. No file handles, sockets, or native resources held. GC-eligible after run completes. No unbounded resource concern. | PASS |
| Audit / governance | Governance enforcement flows through IAgentRunner → content-safety exception → AgentTurnExecutor catch → terminal-safety-failed edge. This path is unchanged — the Workflow graph structure is identical, only instantiation timing changes. | PASS |

**Pre-existing advisories** (unchanged by this fix, previously tracked):
- F1: Checkpoint JSON unencrypted at rest (tracked since seraph-maf-hitl-design).
- No new advisories introduced.

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-09T18:38:00-07:00 — Post-Implementation Security Review: Per-Run Workflow Construction (seraph-perrun-workflow-bugfix)

**Scope**: Shipped diff — `RunWorkflowFactory.cs` (per-run `BuildWorkflow()`) + new test `SequentialRuns_SecondRunSucceeds_NoOwnershipConflict`.

**Verdict**: 🟢 PASS — implementation matches pre-approved design.

| Area | Finding | Rating |
|------|---------|--------|
| Diff matches approved design | Confirmed: no cached `_workflow` field or public `Workflow` property. `StartAsync` (line 161) and `ResumeAsync` (line 171) each call `var workflow = BuildWorkflow();` and pass the fresh instance to `InProcessExecution`. Shared `_checkpointManager` and `_checkpointDir` retained as approved. | PASS |
| No secrets in test | `WorkflowIntegrationTests.cs` Test 5 uses only factory-provided `TestApiKey` constant (not a real credential), `test@localhost` signature, and GUID-based temp paths. No hardcoded secrets or tokens. | PASS |
| Governance / audit regression | Content-safety terminal edge (Guardrail 6, lines 129-130) and worktree cleanup (Guardrail 8, line 179 `DeleteCheckpoints`) remain intact. Test 4 explicitly validates safety-failed runs never expose diffs. No regression. | PASS |
| Concurrent checkpoint writes | `FileSystemJsonCheckpointStore` is not documented as thread-safe; concurrent runs write to **different** `{checkpointDir}/{runId}/` subdirectories. Same-runId concurrency is gated by `RunWorkflowRegistry` + status transitions. Classification: **RELIABILITY concern, not SECURITY** — no cross-run data exposure possible since paths are GUID-partitioned. Deferred follow-up (non-blocking). | INFO |

**Deferred follow-up (reliability, not security)**:
- R1: Confirm or add explicit file-locking if `FileSystemJsonCheckpointStore` is ever called concurrently for the same runId (currently prevented by registry guard — no action needed today).

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-09T19:57:54-07:00 — Post-Implementation Security Review: Issue 3 — list_directory + Sandbox Root Equality Fix

**Scope**: `SandboxPathValidator.cs` (exact-root equality branch + VerifyOpenedHandle), `SandboxedFileTools.cs` (A2 reparse-root check + ListDirectoryAsync), `FoundryAgentRunner.cs` (list_directory tool registration + BuildTools).

**Verdict**: 🟢 PASS

### Pre-Implementation Advisory Follow-Up

| Advisory | Status | Evidence |
|----------|--------|----------|
| **A2** — Assert sandbox root is not a reparse point at construction (reject, not warn) | ✅ IMPLEMENTED | `SandboxedFileTools.cs:19-26` — constructor checks `FileAttributes.ReparsePoint` on `DirectoryInfo(_sandboxRoot)` and throws `SandboxViolationException`. Covers symlinks AND junctions. Hard rejection. |
| **A3** — ListDirectoryAsync must be non-recursive and must NOT output symlink target paths | ✅ IMPLEMENTED | `SandboxedFileTools.cs:153-158` — `RecurseSubdirectories = false`, `AttributesToSkip = FileAttributes.ReparsePoint`. Only `entry.Name` emitted. |

### Detailed Findings

| # | Check | Rating |
|---|-------|--------|
| 1 | Exact-root equality: triggers only on exact case-insensitive `string.Equals` — sibling-prefix rejected | PASS |
| 2 | "..", absolute, device, UNC, drive-relative still rejected (steps 1-3 + ValidateAbsoluteContained) | PASS |
| 3 | Reparse ancestor walk runs unconditionally for all paths (line 47); root self-check by A2 at construction | PASS |
| 4 | ListDirectoryAsync gates on `ValidateAndResolve` — out-of-sandbox paths return `Rejected` | PASS |
| 5 | Non-recursive: `RecurseSubdirectories = false`, `ReturnSpecialDirectories = false` | PASS |
| 6 | No symlink targets exposed: `AttributesToSkip = ReparsePoint` skips them entirely; only `Name` used | PASS |
| 7 | Governance (Principle X): dual-layer — YAML `allow-file-read-or-list` + SandboxPolicyBackend `KnownFileTools` includes `list_directory`; denied calls rejected at both layers | PASS |
| 8 | LLM output: only `[dir] {Name}` / `[file] {Name}` returned — no absolute paths or PII leaked | PASS |
| 9 | A2 covers junctions: `FileAttributes.ReparsePoint` set for both symlinks and junctions on Windows | PASS |
| 10 | VerifyOpenedHandle also updated with equality branch (line 158-160) — consistent | PASS |

### Test Evidence

57/57 sandbox security tests pass. Coverage includes exact-root ".", sibling-prefix, absolute escape, device/UNC, traversal, symlink escape, and governance deny-all-other scenarios.

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-09T20:30:00-07:00 — Scribe Security Verdict Backfill: Issues 3 and 6

**Scope**: Session bugfixes for Issue 3 (`SandboxPathValidator`, `SandboxedFileTools`, `FoundryAgentRunner`) and Issue 6 (`WorktreeManager.RemoveWorktree`, hybrid merge teardown tests, API docs).

**Verdicts recorded from completed reviews**:

| Issue | Phase | Reviewer verdict | Notes |
|-------|-------|------------------|-------|
| Issue 3 — sandbox root `.` + `list_directory` | Pre-implementation | 🟡 Advisory | Seraph advisory A2/A3 required root reparse rejection and non-recursive list output without symlink targets; both were implemented. |
| Issue 3 — sandbox root `.` + `list_directory` | Post-implementation | 🟢 PASS | Follow-ups for TOCTOU documentation and CancellationToken wiring were applied after the post-review. Existing detailed Issue 3 Seraph entry remains above. |
| Issue 6 — worktree teardown reorder | Pre-implementation | 🟢 PASS | Seraph approved directory-delete-first plus fresh repository-handle design. |
| Issue 6 — worktree teardown reorder | Post-implementation | 🟢 PASS | Seraph confirmed implementation passed; comment-accuracy follow-up was applied. |

**Reviewer evidence source**: completed session spawn manifest provided to Scribe.  
**Recorded by**: Scribe

## 2026-06-09T21:30:00-07:00 — Post-Implementation Security Review: Issues 1, 2, 4, 5 — Suppression Allowlist, Blocked Loop-Back, merge.started Event

**Scope**: GitHubCopilotAgentRunner.cs, FoundryAgentRunner.cs (Issue 1/2); WorktreeManager.cs, MergeExecutor.cs, RunWatchLoopService.cs, RunWorkflowFactory.cs (Issue 4); EventTypes.cs, Program.cs, sse.ts, RunWatcher.tsx (Issue 5).

---

### ISSUE 1 and 2 — Tool-Event Suppression (GitHubCopilotAgentRunner / FoundryAgentRunner)

**Verdict**: 🟢 PASS

| # | Check | Finding | Rating |
|---|-------|---------|--------|
| 1 | Allowlist is static, not model-driven | `SuppressedInternalTools` is `private static readonly HashSet<string>` initialized with literal strings `"report_intent"`, `"glob"`. Never mutated. No model-controlled input flows into it. | PASS |
| 2 | Audit signal preservation | `report_intent`/`glob` are SDK-internal housekeeping tools that do not carry permission-handler audit signals. Permission-handler logging (sandbox allow/deny decisions) is independent and emitted via `ILogger`/governance events, not via SSE tool-call events. Suppression does not remove security-relevant observability. | PASS |
| 3 | Unbounded-growth DoS (suppressedCallIds) | `suppressedCallIds` is declared as a local `HashSet<string>` inside `ExecuteAsync`, scoped to a single run invocation. It is GC-eligible on method return. Maximum size bounded by the number of suppressed-tool calls per run (at most 2 tool types, typically single-digit invocations). No static/cross-run accumulation. | PASS |
| 4 | run.completed removal — sandbox/worktree teardown | Confirmed: the old `Emit("run.completed", ...)` was a *cosmetic SSE event*. Sandbox teardown is gated on `HandleTerminalOutputAsync` / `FailRunSafeAsync` in `RunWatchLoopService.cs` which triggers on `WorkflowOutputEvent` from the MAF workflow stream — independent of any SSE event emitted by the runner. Replacing with `agent.turn.end` does not skip any cleanup path. FoundryAgentRunner similarly only removed the cosmetic emit after the `completedNormally = true` break; the actual run lifecycle is managed by the watch loop. | PASS |
| 5 | FoundryAgentRunner list_directory addition | New tool `list_directory` is already registered in `SandboxPolicyBackend.KnownFileTools` (confirmed in Issue 3 post-impl review). Tool delegates to `SandboxedFileTools.ListDirectoryAsync` which enforces path validation. No new injection surface. | PASS |

---

### ISSUE 4 — Platform-Conditional HEAD Compare + Blocked Loop-Back

**Verdict**: 🟡 ADVISORY

#### 4A: Platform-conditional HEAD compare (WorktreeManager.cs)

| # | Check | Finding | Rating |
|---|-------|---------|--------|
| 1 | Branch identity validated before HEAD compare | Confirmed: `repo.Branches[originatingBranch]` at line 107 performs the canonical libgit2 branch lookup (case-sensitive on Linux, filesystem-native on Windows). This validates existence via the actual ref store. The subsequent HEAD compare (line 143) uses OrdinalIgnoreCase on Windows solely to determine whether to use MergeCheckedOut vs MergeRefOnly codepath — not to select which branch to merge INTO. The merge target is always the already-resolved `origin` Branch object. No confused-deputy possible. | PASS |
| 2 | originatingBranch not attacker-creatable | Confirmed: `originatingBranch` comes from `run.OriginatingBranch` set at run-creation time from the authenticated API request (Program.cs line 97). The API validates it is non-empty and the caller is authenticated via `ApiKeyAuthMiddleware`. The branch must already exist in the repository (`repo.Branches[originatingBranch]` throws on miss). An attacker cannot inject an arbitrary branch name that resolves differently via case on Windows because libgit2 resolves via the filesystem-native ref (same case-insensitivity as the subsequent HEAD compare). | PASS |

#### 4B: Blocked to Review Loop-Back (RunWorkflowFactory.cs)

| # | Check | Finding | Rating |
|---|-------|---------|--------|
| 1 | Unbounded loop (DoS) | The cycle is: `mergeBinding` -> `blockedAdapter` -> `reviewBinding` (review port). The review port is a MAF `ExternalRequestPort` — it SUSPENDS the workflow and emits a `RequestInfoEvent`. The watch loop (line 78-106) handles this by storing the pending request and transitioning to `AwaitingReview`. **The workflow halts until a human submits a review decision via the `/api/runs/{id}/review` endpoint.** Each cycle requires an explicit human HTTP POST. An attacker who can submit unlimited reviews could loop indefinitely, but: (a) the review endpoint requires authentication, (b) each cycle re-validates via `IsWorkingTreeMergeSafe` in `MergeCheckedOut`, (c) no resources accumulate between cycles (workflow is checkpointed and suspended). This is bounded by human action — no auto-retry. | PASS |
| 2 | Merge safety re-run each cycle | Confirmed: each time `mergeBinding` (MergeExecutor) runs, it calls `MergeWorktree` -> `WorktreeManager.MergeWorktree` which invokes `IsWorkingTreeMergeSafe` for the checked-out path. There is no bypass — the clean-check runs every merge attempt regardless of how many times the workflow has cycled. | PASS |
| 3 | Registry removal + no terminal status (resource leak) | **ADVISORY**: If a "blocked" `MergeOutput` somehow reaches the `WorkflowOutputEvent` handler in `RunWatchLoopService` (lines 108-112), the code logs a warning (line 141-142) but then unconditionally executes `_registry.Remove(runId)` (line 110) and `_factory.DeleteCheckpoints(runId)` (line 111). This would orphan the run: `awaiting_review` status in DB, no active watch loop, no checkpoints to resume from. The workflow graph design makes this path unreachable (blocked -> blockedAdapter -> reviewBinding, not terminalMerge), but the defensive handler should either: (a) NOT remove from registry/delete checkpoints, or (b) transition to a terminal failed state. Current code is a latent integrity risk under graph framework bugs. | ADVISORY |

**Required mitigation for 4B-3**:
- In `RunWatchLoopService.HandleTerminalOutputAsync`, when `mergeOutput.Status == "blocked"`, either (a) skip `_registry.Remove` + `_factory.DeleteCheckpoints` (early return before line 110), or (b) call `FailRunSafeAsync` to set terminal status and clean up properly. Option (a) is safer — if the workflow somehow emits this as output, leaving the run registered allows the restart service to pick it up.

---

### ISSUE 5 — merge.started Event + Frontend Dedup

**Verdict**: 🟢 PASS

| # | Check | Finding | Rating |
|---|-------|---------|--------|
| 1 | Payload contains only tree_hash | Both emit sites (Program.cs line 360, line 449) emit `new { tree_hash = run.TreeHash }`. No repositoryPath, worktreePath, worktreeBranch, or originatingBranch in payload. TreeHash is a git SHA (public commit metadata). | PASS |
| 2 | No spoofing surface | SSE is server-to-client only (unidirectional). The `merge.started` event is emitted exclusively from server-side code in the authenticated review handler. No client can inject or forge SSE events. The EventTypes constant is server-defined. | PASS |
| 3 | Frontend dedup guard | The new `SINGLETON_EVENT_TYPES` set gates dedup only on `seq === 0` events (events without a sequence number, which can occur on SSE reconnect replay). This prevents duplicate rendering of lifecycle events when SSE reconnects deliver the same event twice. It does NOT weaken the primary sequence-based dedup (line 163: `seq > 0 && prev.some(e => e.sequence === seq)`). A replayed event with a valid sequence is still rejected by the sequence check. The set is `ReadonlySet<string>` — immutable at runtime. `merge.started` is intentionally NOT in the singleton set (it is not terminal and could theoretically repeat in a blocked->retry cycle). | PASS |
| 4 | No path/sensitive data in RunWatcher.tsx | The `effectiveReviewComplete` derivation reads only `merged_commit_hash` and `reason` from event payloads — both are sanitized server-side (SHA strings and block-reason text). No filesystem paths rendered. | PASS |

---

### Summary

| Issue | Verdict | Mitigations Required |
|-------|---------|---------------------|
| Issues 1 and 2 (Suppression) | 🟢 PASS | None |
| Issue 4A (HEAD compare) | 🟢 PASS | None |
| Issue 4B (Blocked loop-back) | 🟡 ADVISORY | 4B-3: Guard `HandleTerminalOutputAsync` so the "blocked" defensive branch returns before registry-remove/checkpoint-delete, preventing orphan-run state under MAF framework bugs. |
| Issue 5 (merge.started) | 🟢 PASS | None |

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-09T20:30:00-07:00 — Pre-Implementation Security Review: A2 Fix — RepositoryRootValidator (repository path allowlist)

**Scope**: Proposed `RepositoryRootValidator` singleton in `apps/Scaffolder.Api/Security/` — closes A2 (arbitrary repository path traversal via `POST /api/runs`). Design validates and canonicalizes `repository_path` before `Run` construction; enforces optional `Runs:AllowedRepositoryRoots` allowlist with symlink-following containment check.

**Verdict**: 🟡 ADVISORY — design is fundamentally sound and closes A2 when configured; implementation must address the must-fix items below.

### Must-Fix (blocking for implementation sign-off)

| ID | Finding | Rationale |
|----|---------|-----------|
| **M1** | 8.3 short-name normalization on Windows | `Path.GetFullPath` does NOT expand 8.3 short names (`C:\PROJEC~1` stays as-is). If symlink resolution uses only `Directory.ResolveLinkTarget` / `FileInfo.ResolveLinkTarget`, short names remain un-normalized. Implementation MUST use a resolution API that also canonicalizes 8.3 names (e.g., `GetFinalPathNameByHandle` with `FILE_NAME_NORMALIZED` — same technique as `SandboxPathValidator.GetFinalPathWindows`). Both input AND allowlist roots must be resolved through the same mechanism. |
| **M2** | Alternate Data Streams (ADS) rejection | Paths containing `:` beyond the drive-letter position (index 1) must be rejected. E.g., `C:\root\repo::$DATA`, `C:\root\repo:stream`. `Path.GetFullPath` does not strip ADS suffixes. Add to the early-reject checks: if `path.IndexOf(':', 2) >= 0` on Windows, reject with categorical error. |
| **M3** | Non-existent path resolution fallback must not create an oracle | When symlink resolution fails (target doesn't exist), the error returned to the caller MUST be indistinguishable from "not within an allowed repository root." If the design surfaces a different error for "resolution failed" vs "not in allowlist," an attacker can probe path existence on the server. Specify: resolution-failure → same categorical 400 as allowlist-miss. |

### Advisory (non-blocking, should-fix or document)

| ID | Finding | Rationale |
|----|---------|-----------|
| **A1** | TOCTOU between validate-at-submission and use-at-WorktreeManager | A symlink retarget between `ValidateAndCanonicalize` (submission time) and `new Repository(path)` (execution time) bypasses the containment check. Risk is low in the target threat model (attacker needs filesystem write to a validated ancestor), but for shared/multi-tenant: (a) document as accepted residual risk, OR (b) add lightweight re-validation in WorktreeManager before `new Repository(path)`. |
| **A2** | Permissive default requires prominent documentation | Empty `AllowedRepositoryRoots` → permissive mode is acceptable for the local-first product, but operators of shared/exposed deployments will be silently insecure unless they read docs. Requirements: (1) warn-log at startup MUST be `LogWarning` or higher; (2) deployment/security docs MUST contain a "Hardening" section with explicit allowlist guidance; (3) consider: if bound to non-loopback AND allowlist empty, escalate to `LogError`. |
| **A3** | Separator-boundary edge: root with trailing separator | Ensure the containment check normalizes trailing separators on both resolved-path and resolved-root before comparison. Trim trailing separator from root before appending separator for prefix check (matching `SandboxPathValidator` pattern at line 129). |
| **A4** | S2 error-message audit across all rejection paths | Confirm NO rejection path echoes the caller's input or any server filesystem path. The 5 rejection categories should all use static categorical strings. Additionally, warn-log in permissive mode MUST NOT log the user-supplied path at Warning level (log-injection risk); use Debug or redacted indicator. |

### Security Questions — Answers

| Q# | Answer |
|----|--------|
| 1 | YES with caveats — closes A2 when configured, provided M1/M2/M3 addressed. No escape via `..`, Unicode, case folding, or trailing-dot/space. |
| 2 | Low residual TOCTOU risk — acceptable with documentation (A1). Re-validate-at-use is ideal defense-in-depth but not blocking. |
| 3 | Permissive default acceptable for local-first authenticated product. Must pair with docs + warn-log (A2). |
| 4 | Containment boundary correct — use `root + sep` prefix, normalize trailing separators (A3), handle root equality. |
| 5 | S2 sufficient — categorical messages don't echo input. One gap: M3 (resolution-failure oracle). |
| 6 | M1, M2, M3 above; reuse `SandboxPathValidator.GetFinalPathWindows` pattern for consistency. |

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-09T20:55:00-07:00 — Post-Implementation Security Review: A2 Path Traversal Fix (RepositoryRootValidator)

**Scope**: Shipped implementation of `RepositoryRootValidator`, `RealPath.Resolve`, DI wiring in `Program.cs`, and 22 new tests. Confirms closure of A2 (arbitrary repo path traversal via `POST /api/runs`).

**Verdict**: 🟢 PASS — All three pre-review must-fixes (M1/M2/M3) are correctly implemented. Vulnerability is closed when allowlist is configured.

| Area | Finding | Rating |
|------|---------|--------|
| **M1 — 8.3 / symlink normalization** | `RealPath.ResolveWindows` uses `GetFinalPathNameByHandle` with `FILE_NAME_NORMALIZED` flag. Allowlist roots are resolved through the same API at startup. Both sides use identical resolution — 8.3 names and ancestor symlinks are fully canonicalized before comparison. | PASS |
| **M2 — ADS rejection** | Raw input is scanned for `:` at any position other than index 1 (drive letter) BEFORE `GetFullPath`. Alternate data stream suffixes are rejected. | PASS |
| **M3 — No path-existence oracle** | `IOException` from `RealPath.Resolve` is caught and re-thrown with the same `PathRejectedMessage` constant used for allowlist miss. Caller cannot distinguish non-existent from not-allowed. | PASS |
| **Sink isolation** | `Program.cs` stores validator output on `Run.RepositoryPath`. `RunOrchestrator.StartRunAsync` passes `run.RepositoryPath` to `WorktreeManager.AddWorktree`. No alternate path flows bypass the validator. | PASS |
| **UNC/device post-canonicalization** | Re-applies `RejectUncAndDevicePaths` to the `Path.GetFullPath` output, blocking the edge where `GetFullPath` could produce extended-length paths. | PASS |
| **Error-message hygiene (S2)** | All 9 throw-sites use static categorical strings. No rejection path echoes caller input or server filesystem paths. Startup warn-log contains no paths, only guidance text. No request body logging introduced. | PASS |
| **Permissive-default documentation** | Docs state "Shared, exposed, or multi-tenant deployments MUST configure this." Startup `LogWarning` fires exactly once (singleton constructor). Language is explicit and actionable. | PASS |
| **TOCTOU residual** | Documented in XML remarks on `ValidateAndCanonicalize`. Accepted under the authenticated-API threat model. No degradation from pre-review assessment. | ACCEPTED |
| **Separator-boundary correctness** | Root TrimEnd's both separators; prefix check uses `root + DirectorySeparatorChar`. Since `GetFinalPathNameByHandle` normalizes to native separators, no mixed-separator bypass is possible. Exact-match handles root-is-repo case. | PASS |
| **Test coverage** | 22 tests: 7 rejection categories assert `Throws` (prove rejection). Symlink tests guard with early-return on privilege errors (effective skip on unprivileged Windows). Integration test confirms end-to-end 400 via `WebApplicationFactory`. | PASS |

**Advisory findings (non-blocking)**:

| ID | Finding | Priority |
|----|---------|----------|
| A1-post | Symlink tests use early-return instead of `[Skip]` attribute — test runners won't report them as "skipped" in output. Cosmetic; no security impact. | LOW |

**No must-fix findings. No secrets, PII, or sensitive data introduced.**

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-11T01:30:07-07:00 — Post-Implementation Security Review: B3 request-changes feedback loop

**Scope**: Uncommitted diff for `POST /api/runs/{id}/request-changes`, revision workflow restart, run revision audit storage, and shell approval reset. Reviewed under STRIDE and Constitution Principle X (safe execution: sandboxed, bounded, human-gated, auditable).

**Verdict**: 🔴 BLOCK — request-changes must not ship until the decision-race and prompt-delimiter issues are fixed.

| ID | STRIDE | Severity | Evidence | Finding | Mitigation |
|----|--------|----------|----------|---------|------------|
| B3-S1 | Tampering / Elevation of Privilege | **Blocker** | `apps/Scaffolder.Api/Program.cs:1135-1139` | Reviewer feedback is sanitized for control chars and length-bounded, but is inserted verbatim inside a plain XML-like `<reviewer_feedback>` block. A malicious reviewer can include a matching close tag and place system-prompt-like text outside the intended untrusted block, weakening the delimiter boundary before the fresh coding agent runs. | Encode/escape delimiter syntax before prompt assembly (e.g., XML/JSON string encoding), or use a nonce-delimited envelope where the nonce is generated server-side and any occurrence in user text is escaped/rejected. Add explicit wording that all reviewer text is untrusted data, not commands, and add tests for delimiter-breakout payloads. |
| B3-S2 | Tampering / DoS | **Blocker** | `apps/Scaffolder.Api/Program.cs:377-430`, `apps/Scaffolder.Api/Program.cs:561-588`, `apps/Scaffolder.Api/Runs/RunWatchLoopService.cs:134-172`, `apps/Scaffolder.Api/Infrastructure/SqliteRunStore.cs:215-226` | `/review` consumes the pending request and sends a decision to the paused workflow without first moving the DB row out of `awaiting_review`; `/request-changes` independently wins `AwaitingReview -> InProgress`, unregisters the workflow, and starts a fresh workflow on the same worktree. A concurrent approve/request-changes race can let the stale workflow continue and emit terminal output; `TrySetTerminalStatusAsync` accepts any non-terminal status, so stale merge-failed output can mark the new revision `merge_failed`, complete the stream, remove the registry entry, and delete checkpoints for the fresh workflow. | Introduce a single per-run review-decision lock/CAS shared by approve, decline, and request-changes. Make `/review` transition status before sending the external response, or make request-changes atomically consume the pending request and fail if already consumed. Bind registry/checkpoint operations and terminal status writes to a revision/generation token or expected `StreamingRun` instance so stale workflows cannot mutate fresh revisions. |
| B3-S3 | Repudiation / DoS | **Major** | `apps/Scaffolder.Api/Program.cs:548-603`, `apps/Scaffolder.Api/Infrastructure/SqliteRunRevisionStore.cs:37-49` | The revision cap is enforced from `run_revisions`, but insertion of the audit row is non-fatal after the CAS succeeds. If the insert fails after status moves to `in_progress`, the agent still starts, the revision is not counted toward `Runs:MaxRevisions`, and Principle X audit is incomplete. Repeated transient DB failures could bypass the cost/compute cap and erase who/what/when evidence for revisions. | Make the status transition and revision insert atomic, or fail closed: if the audit insert fails, do not start the revision and restore/fail the run deterministically. Treat audit persistence as mandatory for starting a fresh agent execution. |
| B3-S4 | Elevation of Privilege / Tampering | **Major** | `apps/Scaffolder.Api/Program.cs:756-765`, `packages/Scaffolder.AgentTools/Tools/RunCommandTool.cs:22-50`, `apps/Scaffolder.Api/Program.cs:587-588` | B3 correctly clears run-scoped shell approvals before starting the new revision, but the approval endpoint accepts any authenticated API key and does not verify run ownership, run status, revision generation, or a pending tool-approval nonce. Another caller who can learn or compute a command hash can re-seed an approval for someone else's restarted run after the clear, causing the agent to auto-execute a destructive command on retry. | Add `HttpContext` + `SqliteRunStore` owner/status checks to shell approval submission; require the run owner and an active approval-required event for the current revision/generation. Prefer per-tool-call nonces over stable command hashes, or bind approvals to `(runId, revision, requestId, commandHash)` and expire after one use. |
| B3-S5 | Information Disclosure | **Minor** | `apps/Scaffolder.Api/Program.cs:525-527` | Non-owners receive `403` for existing runs and `404` for missing runs, allowing authenticated callers to distinguish valid run IDs. This matches `/review`, but differs from artifact endpoints that hide non-owned runs with `404`. Run IDs are high entropy, so practical risk is low. | Return `404` for non-owned runs on request-changes (or standardize all run endpoints on non-enumerating responses) unless callers require explicit forbidden semantics. |
| B3-S6 | Repudiation | **Minor** | `apps/Scaffolder.Api/Infrastructure/SqliteDb.cs:96-105`, `apps/Scaffolder.Api/Infrastructure/SqliteRunRevisionStore.cs:24-49` | `run_revisions` is append-only by convention only. The store exposes only insert/read-max methods, but the SQLite schema lacks triggers preventing update/delete, despite the audit table being the Principle X evidence record. | Add SQLite triggers preventing `UPDATE` and `DELETE` on `run_revisions`, matching the stated append-only audit model. |

**Positive checks**:
- Comment validation strips C0/C1 controls and enforces 8000-character input cap before prompt assembly (`Program.cs:538-546`, `Program.cs:1094-1121`).
- Reviewer identity is captured from the authenticated caller, not client-supplied (`Program.cs:528`, `Program.cs:595-597`).
- Request-changes owner check is present and mirrors `/review` (`Program.cs:525-536`).
- Raw reviewer text is not emitted to the SSE stream or structured logs in this path; stream events carry only revision numbers (`Program.cs:605-617`).

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-11T01:59:45-07:00 — Re-Review: B3 request-changes feedback loop fixes

**Scope**: Current uncommitted diff for B3 fixes in API workflow/review paths, revision audit store, shell approvals, and client request-changes wiring.

**Verdict**: 🟡 PARTIAL PASS — B3-S2 through B3-S6 are closed; one sanitizer hardening gap remains from B3-S1.

| ID | Status | Evidence | Finding |
|----|--------|----------|---------|
| B3-S1 | PARTIAL | `apps/Scaffolder.Api/Program.cs:1159-1178` | Nonce fence and untrusted-data framing are present, and lowercase delimiter/fake-nonce repeats are stripped. However stripping is ordinal/case-sensitive, so case-varied reviewer-feedback tags remain in the fenced body. Treat as a remaining prompt-delimiter hardening gap; strip tag forms case-insensitively before embedding. |
| B3-S2 | PASS | `Program.cs:380-391`, `RunWatchLoopService.cs:151-157`, `RunWorkflowRegistry.cs:39-46` | Review decisions now CAS out of `awaiting_review`; stale terminal output no-ops on generation mismatch; abandon cancels/disposes old workflow CTS without failing the replacement. |
| B3-S3 | PASS | `Program.cs:602-618`, `SqliteRunRevisionStore.cs:24-50` | Revision audit insert is mandatory before starting the new agent revision; failure marks run failed and emits `run.failed`. Cap is backed by durable revision max and CAS prevents concurrent bypass. |
| B3-S4 | PASS | `Program.cs:774-795`, `InMemoryShellApprovalStore.cs:18-25`, `RunCommandTool.cs:25` | Shell approval submission is owner/status-gated; approvals are cleared on revision and consumed atomically via `TryRemove`. |
| B3-S5 | PASS | `Program.cs:530-538` | Request-changes returns 404 for missing or non-owned runs. |
| B3-S6 | PASS | `SqliteDb.cs:107-117` | SQLite triggers abort UPDATE and DELETE on `run_revisions`. |

**Fresh STRIDE notes**: No new spoofing/IDOR path found in shell approvals or request-changes. Merge CAS broadens coordinator acceptance for already-`merging` runs to support the endpoint pre-CAS; keep this path internal-only and covered by review endpoint authorization. Targeted tests passed: 24/24 security/race/request-changes/watch-loop/CAS tests.

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-11T02:18:21-07:00 — Final Security Re-Review: B3 request-changes fixes

**Scope**: Final uncommitted diff review for B3 request-changes after case-insensitive reviewer-feedback delimiter stripping and three recovery/liveness fixes.

**Verdict**: 🟢 PASS — prior MAJOR is closed; no new security findings.

| Area | Evidence | Finding | Rating |
|------|----------|---------|--------|
| Case-insensitive delimiter stripping | `apps/Scaffolder.Api/Program.cs:1185-1188`, `tests/Scaffolder.Tests/SecurityAndRaceTests.cs:78-120` | `StringComparison.OrdinalIgnoreCase` is applied to both opening and closing reviewer_feedback tag prefixes. This covers ASCII case variants using culture-invariant ordinal semantics. Unicode confusables are not equivalent to the ASCII delimiter and cannot form the synthetic fence. | PASS |
| Nonce handling | `apps/Scaffolder.Api/Program.cs:1182-1188` | The nonce remains stripped with exact ordinal comparison, but it is generated after receiving the comment and the tag-prefix stripping already removes delimiter structure. No practical breakout remains; `OrdinalIgnoreCase` for nonce would be optional defense-in-depth only. | PASS |
| Stream liveness timestamp | `apps/Scaffolder.Api/Infrastructure/RunStreamStore.cs:113-141`, `apps/Scaffolder.Api/Infrastructure/RunStreamStore.cs:249-257` | `LastActiveAt` updates under the same per-entry lock as history append and only affects stale-entry eviction. It is not exposed to clients and does not enable replay or timing disclosure beyond existing event delivery timing. | PASS |
| Approve SendResponseAsync failure recovery | `apps/Scaffolder.Api/Program.cs:448-465`, `apps/Scaffolder.Api/Infrastructure/SqliteRunStore.cs:215-232` | Catch path logs exception server-side, persists generic `send_response_failed`, emits generic `run.failed`, completes the stream, and returns a generic 500 body. No exception text, checkpoint data, paths, or workflow internals leak to the client. The DB transition is a single guarded update from non-terminal state; terminal states cannot be overwritten. | PASS |

**Validation**: `dotnet test .\tests\Scaffolder.Tests\Scaffolder.Tests.csproj --filter "FullyQualifiedName~SecurityAndRaceTests|FullyQualifiedName~SqliteRunStoreCasTests|FullyQualifiedName~RunStreamStoreTests" --no-restore` → 25/25 passed.

**Reviewer**: Seraph | **Requested by**: Ahmed Sabbour

## 2026-06-17T12:06:12-07:00 — RAI Advisory Pass: Feature 008 Phase 1 Coordinator Agent (008-coordinator-agent)

**Scope**: apps/Scaffolder.Api/Coordinator/* (WorkflowFactory, RunService, Messages), Memory/OutcomeSpec.cs + MemoryDbContext, Casting/CastingService.cs (coordinator provisioning), packages/Scaffolder.Squad/Catalog/Resources/agents/coordinator.agent.md, Program.cs orchestration endpoints, apps/web outcome-spec panel + dialog + page.

**Verdict**: YELLOW (advisory recommendations; nothing blocking)

| Area | Finding | Rating |
|------|---------|--------|
| Credentials / secrets | No hardcoded secrets, keys, or connection strings in any reviewed file. CopilotAIAgent draft turn passes apiKey:null. | PASS |
| Human-confirmation gate | Genuinely enforced: Phase 1 has NO dispatch path. Workflow is draft -> RequestPort suspend -> confirm/decline finalize (terminate) | revise re-draft. FinalizeAsync only sets status; no child run, no workspace mutation. Charter Boundaries forbid pre-confirmation dispatch. (Principle IX/X) | PASS |
| Authorization (owner-scoping) | GET/confirm/revise outcome-spec endpoints all enforce IsOwner (403 on mismatch) + atomic pending-gate consume for replay/double-POST. POST /orchestrations follows existing POST /runs convention (project existence + workspace checks; no project-owner gate) — consistent, no new gap. | PASS |
| PII | ConfirmedBy / SubmittingUser store GitHub login only (caller.User); no emails or PII beyond login. Matches Feature 006 convention forbidding git emails. | PASS |
| Content / inclusive language / emoji | User-facing strings are neutral, non-deceptive, accessible (aria-labels, hidden decoratives). No emojis in any new file (Principle VIII verified by surrogate-pair scan). | PASS |
| Prompt injection (advisory) | input.Goal and input.ReviseFeedback are interpolated into the model task prompt without explicit delimiting; drafting CopilotAIAgent runs against the real repo working dir with sandbox + IToolApprovalGate. Phase 1 impact is bounded (no dispatch; output is JSON-parsed and React-escaped as text, no dangerouslySetInnerHTML, no SQL string-building). Recommend fencing user goal/feedback and re-validating before Phase 2 dispatch consumes the confirmed spec. | ADVISORY |

**Reviewer**: Rai | **Requested by**: Ahmed Sabbour (@sabbour)

## 2026-06-17T17:15:00-07:00 — RAI Phase 2 Review: Feature 008 Collective Coordinator Surface (008-coordinator-agent)

**Scope**: Phase 2 uncommitted surface only — CoordinatorOrchestratorExecutor.cs (decomposition prompt), CoordinatorSteeringService.cs + CoordinatorDispatchService.cs (steering), RunOrchestrator.StartRevisionAsync/StartChildRunAsync (trimmed child pipeline), event payloads (coordinator.topology / subtask.* / coordinator.steering / coordinator.work_plan), Contracts/Dtos.cs + Program.cs endpoints (work-plan/children/steer), apps/web topology view, apps/Scaffolder.Mcp CoordinatorTools.cs + docs/reference/*. OUT OF SCOPE: appsettings.Development.json pre-existing committed secrets (known, not introduced by Phase 2).

**Verdict**: GREEN — no blockers (one INFO recorded for forward awareness)

| Area | Finding | Rating |
|------|---------|--------|
| Prompt injection (decomposition) | Confirmed-spec fields fenced <<<SPEC>>>/<<<END_SPEC>>> with explicit "untrusted DATA, never instructions" directive; consistent with Phase 1 drafting fencing. No unfenced user/model-derived interpolation. | PASS |
| Steering instruction injection | Operator-authored, owner-gated, applied via StartRevisionAsync as a sanctioned next-turn directive bounded by child content-safety + sandbox. Correctly NOT fenced (it is a legitimate instruction). Not a hijack vector under owner-trust boundary. | PASS |
| stop cancellation safety | Abandon -> Cts.Cancel (real mid-turn token cancel) + authoritative terminal run.cancelled; dispatch loop (single writer) transitions subtask -> failed. Steering never writes subtask rows. | PASS |
| Steering validation | pause rejected pre-persistence (never exposed/executed); unknown verbs rejected; blank instruction for redirect/amend rejected; maps to 400. | PASS |
| Child trimmed pipeline | isChild:true (agent + RAI assemble-ready; no per-child review/merge/scribe). No bypass of COLLECTIVE gate (Phase 3, intended). | PASS |
| PII / secret / path exposure | Events + DTOs expose only ids/GUIDs, role/agent/model names, status, title/scope, worktreeBranch (branch, not abs path), treeHash, createdBy (GitHub login only). No emails, paths, tokens. | PASS |
| API authorization | work-plan/children/steer enforce IsOwner -> 403, 404 missing, 400 malformed. No new IDOR. | PASS |
| Web topology XSS | No dangerouslySetInnerHTML/innerHTML/eval in new components; free text React-escaped. | PASS |
| Honesty (steering/decomposition) | Docs + MCP tool description: no mid-turn interrupt claim; redirect/amend land at next boundary; pause not supported/not exposed. | PASS |

**INFO (non-blocking)**: I1 — The safety of injecting the steering instruction as a child turn depends on the steer endpoint remaining owner-scoped. If widened beyond the run owner or fed from a model/third-party source, the instruction becomes untrusted input and would require DATA-fencing like the decomposition spec. Keep IsOwner -> 403 as the invariant. Owner: Morpheus/Tank.

**Reviewer Rejection Protocol**: not triggered (GREEN). No author lockout; no fix agent assigned.

**Reviewer**: Rai | **Requested by**: Ahmed Sabbour (@sabbour)

## 2026-06-17T20:44:00-07:00 — RAI Review: Feature 008 Phase 2 Remediation (commit d70d1a9)

**Scope**: RunOrchestrator.ComposeChildSystemPrompt + MarkChildRunFailedAsync + PersistFailedRunEventsAsync; CoordinatorDispatchService failure handling + FinalizeDispatchAsync; SqliteRunStore.GetRunsByProjectAsync child filtering; EventTypes.CoordinatorChildrenComplete; apps/web topology view (CoordinatorTopologyGraph, ProjectPage, runKind.ts). 7 new tests.

**Verdict**: YELLOW (one advisory finding; nothing blocking)

| Area | Finding | Rating |
|------|---------|--------|
| Credential / secret / PII exposure (focus #1) | MarkChildRunFailedAsync persists raw `error.Message` into a USER-VISIBLE, durable log: Run.Result, RunFailed event payload `{reason}`, and RunEvents.PayloadJson (retrievable via GET /api/runs/{childRunId}). Source is StartChildRunAsync pre-start failures — primarily LibGit2Sharp worktree errors (RepositoryNotFoundException, InvalidOperationException with branch names) and IO errors. These messages routinely embed absolute filesystem paths (e.g. C:\Users\<login>\... — reveals OS username + internal layout). No git remote/clone-with-token path observed for local worktrees today, so no confirmed secret leak; but the method is GENERIC and will faithfully persist ANY future exception message verbatim (e.g. a token-embedded remote URL if a fetch/clone is ever added to the child-start path). Full diagnostic detail is ALREADY captured server-side via _logger.LogError(ex,...). | ADVISORY |
| Sandbox boundary (focus #2) | ComposeChildSystemPrompt STRENGTHENS isolation. Boundary text mandates all reads/writes stay inside the worktree, explicitly forbids session-state/.copilot/home/temp, and instructs "do not retry the same out-of-sandbox path; adapt and write within the working directory." Removes the prior duplicated coordinator memory stack that had instructed children to write outside their worktree. No instruction weakens or bypasses any safety control. | PASS |
| Prompt injection (focus #3) | Child system prompt = trusted agent charter (resolved from on-disk .squad/agents/<name>/charter.md, not user input) + static boundary literal. No untrusted spec/task content is interpolated into the system prompt; run.Task stays in the DATA/task position (returned as TaskWithHarvest, model-message), preserving the data/instruction separation. Coordinator fences untrusted spec elsewhere (<<<SPEC>>>, prior Phase 2 review). No new injection surface introduced. | PASS |
| Harmful / deceptive / exclusionary content (focus #4) | User-facing copy ("Awaiting assembly", "Finished its part — waiting for collective assembly") is neutral, accurate, and non-deceptive; FinalizeDispatchAsync makes a previously-hung run honestly terminal. No ableist/gendered/exclusionary terms; parent/child run terminology is standard. Decorative icon marked aria-hidden. No emoji. No dangerouslySetInnerHTML in web changes. | PASS |

**Recommendation (advisory, non-blocking)**: Sanitize the persisted child-failure reason before it lands in the user-visible log. WHAT: redact/normalize `error.Message` in MarkChildRunFailedAsync (e.g. map to an exception-type-based safe summary, strip absolute home paths, and cap length) while keeping the full exception in the server-side logger. WHY: prevents OS-username/path PII disclosure now and forecloses verbatim secret leakage if a token-bearing git operation is ever added to the child-start path. HOW: introduce a small redactor (path-masking + allowlist of safe message shapes) applied to `reason` prior to RecordNext/Result persistence; add a test asserting a home-path-bearing message is masked.

**Forward awareness**: Reinforces prior INFO I1 — keep child-start free of token-embedded remote URLs; if added, the redactor becomes mandatory (would escalate to RED without it).

**Reviewer Rejection Protocol**: not triggered (YELLOW). No author lockout; no fix agent assigned.

**Reviewer**: Rai | **Requested by**: Ahmed Sabbour (@sabbour)
