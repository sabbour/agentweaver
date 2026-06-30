# Seraph — History

## Session: 2026-06-07 — Onboarding & Security Review Program Established

**Project:** scaffolders — single-agent file-editing run system  
**Role:** Security Reviewer  
**Onboarding context:** Feature 001 (single-agent run) runs agent tasks in git worktree sandboxes with live event streaming and approval gate. Primary threats: prompt injection (user task + file reads), PII/secret leakage (events/output streams), sandbox path bypass, governance bypass.

**June 7–12 security review arc:** Pre-implementation YELLOW verdict on 001 (14 findings including 3 critical: sandbox bypass, content-safety gap, unauthenticated SSE); post-implementation reviews cleared most findings GREEN (streaming auth gate, per-run Workflow isolation, run-submission 400 mapping, path traversal hardening, sandboxed-execution tool design, tool-output redaction). Phase 6 sandbox policy enrichment also PASS with 2 medium findings (temp subdirectory contamination, network-enable operator visibility) resolved. FR-005 (GitHub unified auth) refined and approved; FR-024/FR-029 critical symlink/reparse issues caught and fixed in Feature 005 commit 3053741. Early review program established: comprehensive finding capture, pre-implementation YELLOW/post-implementation GREEN/RED gates, and deferred-follow-up tracking.

---

## 2026-06-26T09:37:26-07:00 — MCP OAuth 2.1 security design and reviews complete

Seraph designed the MCP OAuth 2.1 authorization/resource-server flow, reviewed T1-T3 as APPROVE-WITH-FIXES, reviewed T4-T7 as ACCEPT-WITH-FIXES, and signed off the JWT-forwarding deviation after requiring issuer/audience pinning and organization-handling fixes. The security-review arc is recorded in the merge-ready session log.

## 2026-06-27T00:58:00-07:00 — Org-auth 403 rate-limit fix security review: APPROVE

Reviewed Tank's GitHubOrgAuthorizationService rate-limit fix (sabbour/mcp-oauth branch, commit f7dc8756). Change A (Authenticate public_members) does not weaken the gate; actually tightens a latent path by enforcing expired-token checks on the public-membership fallback (previously silent bypass). Change B (Rate-limit discriminator) is sound: Inconclusive never cached, fail-closed maintained at every call site, theoretical false-positive (SAML 403 @ rate-limit exactly 0) has zero security impact. Verdict: ✅ APPROVE. No code changes required. Feature deployed to AKS.

## 2026-06-27T02-23-10 — Sandbox Architecture Security Review: 🟢 Option A Approved

Assessed coordinator-in-sandbox threat model and issued final verdict on Morpheus Option A.

**Verdict on naive coordinator-in-sandbox:** 🔴 RED — granting the pod DB + `pods/exec` + GitHub creds + signing key behind the isolation boundary is a net loss; the most injection-exposed component would gain the keys to the kingdom.

**Verdict on hardened pod-per-run (Option A with broker):** 🟢 GREEN, gated on §2 deploy-gating checklist. Design correctly inverts the RED: reasoning contained in Kata-VM; all secrets stay in the API; broker RPC is the new trust boundary.

**Principal established:** *Relocate the reasoning, never the keys.*

**Broker channel risks (must land with the move, not post-ship):** token replay (nonce+jti), capability over-grant (least-privilege claims + quota), confused deputy (broker derives run id from verified token only), SSRF from pod→API loopback (egress names only API ClusterIP:8080). §4 residuals accepted: compromised broker = total compromise (intended); in-run model-token abuse within scope; Kata 0-day; TOCTOU on brokered state.

## 2026-06-27T02:44:51Z — Fleet deep-dive documentation effort complete

Coordinated parallel 12-agent fleet (background mode) to author deep-dive documentation under docs/deep-dive/. Tank (tank-6), Seraph (seraph-1), Link (link-3), Morpheus (morpheus-3), Trinity (trinity-4) all contributed specialized deep-dive docs alongside 7 other agents. All 13 files verified complete; all 12 todos marked done; no source modifications. Cross-agent decision processing: 4 inbox entries merged (3 copilot directives + morpheus-option-a-plan, 25.3 KB). Scribe logs: 10 orchestration entries + 1 session log written. See .squad/log/2026-06-27T02-44-51Z-fleet-deep-dive-docs.md.

## 2026-06-27T03:05:00-07:00 — Spec 018 convergence

Spec 018 supersedes the earlier zero-secrets/broker-heavy sandbox drafts. Locked direction: all agent execution turns run in sandbox pods, coordinator orchestration remains in API/worker tier, pods may use run-scoped credentials or workload identity, no bespoke capability-token broker, durable state moves to Azure PostgreSQL Flexible Server, and web/worker leasing provides horizontal scale.

## 2026-06-27T03:15:00-07:00 — spec018 Q1/Q2/Q3 resolved
- Q1: Seraph resolved transport: HTTP/2+SSE (Option C) is acceptably safe and preferred over gRPC if mTLS/SPIFFE, scoped worker-only NetworkPolicy ingress, Last-Event-ID resume, bounded listener, and strict egress allowlist all hold. Kube-exec-stdio remains the minimalist fallback; gRPC is rejected.
- Q2: Tank resolved P1 may ship on SQLite with `replicas:1` without PostgreSQL if pods never touch DB directly, writes proxy through the single worker/API process, and no second replica is introduced.
- Q3: Morpheus resolved hybrid pod granularity: pod-per-run during active bursts; checkpoint-and-release on RequestPort/HITL or coordinator child-await suspension; re-claim and rehydrate on resume.

## 2026-06-27T03:35:00-07:00 — A2A security verdict
A2A is yellow/GO-with-conditions. Ship kube-exec-stdio for v1; enable A2A only behind `Sandbox:AgentExecutionMode` after exec-stdio bottlenecks and H1-H7 hold: workload-bound TLS/mTLS preference, scoped NetworkPolicy, gated `/v1/card`, Kestrel/SSE limits, DB-checkpoint resume, no egress broadening, pinned/flagged preview library with fallback.

## 2026-06-27T10:38:23-07:00 — Q1 final override
Coordinator/user directive rejects kube-exec-stdio entirely. A2A is the sole transport; Seraph H1-H7 remain mandatory, except H7's live fallback is `Sandbox:AgentExecutionMode=in-api` rather than exec-stdio. Preview-package hot-path risk is accepted with pin/hash, flag rollback, and GA tracking.

## 2026-06-27T22-41-00-07:00 — Key Vault GitHub token store

Seraph, Link, and Tank completed the Key Vault-backed GitHub token-store batch: design contract, AKS/workload identity wiring, Key Vault Secrets Officer role grant, and C# token-store implementation are in place. Open follow-up: move sandbox pods from shared-file fallback to run-scoped claim-time token injection per spec-018 §3.3.

## 2026-06-27T23:12:47-07:00 — MCP OAuth Reauthentication Loop Diagnosis

seraph-5 diagnosed the "MCP server opens browser to re-auth on EVERY chat message" issue. Root cause: 401 errors on every request trigger OAuth flow restart.

**Local installs:** `.mcp.json` omits `Auth__Mcp__Issuer`, `Auth__Mcp__Audience`, `Auth__Mcp__JwksUri`. MCP validates tokens against its own host instead of the API, causing iss/aud mismatch → JWKS fetch fails → token invalid → reauthentication loop repeats.

**AKS install (user's actual case):** Re-investigation in progress. Candidate causes:
- External vs. configured iss/aud mismatch
- JWKS fetch reachability (MCP → API NetworkPolicy)
- Multi-replica ephemeral signing key

**Key insight:** MCP OAuth is a **separate token system** from the GitHub KV token store. Do not conflate:
- **GitHub KV store:** OAuth tokens exchanged with GitHub, stored in Key Vault, used by API to call GitHub APIs
- **MCP OAuth:** RS256 tokens minted by the API (signed with KV secret `mcp-oauth-signing-key`), sent to MCP

Diagnosis complete; root-cause analysis and remediation ready for next cycle.

## 2026-06-28T00:18:00-07:00 — Replica-safe MCP OAuth broker pattern

Security review follow-up: the OAuth broker split-brain was fixed by moving transient auth state to EF-backed `MemoryDbContext` storage and using conditional `ExecuteDeleteAsync` as the atomic claim for pending states and authorization codes. Keep this pattern in mind for future multi-replica hardening of `PendingRequestStore`, `CoordinatorSteeringQueue`, `CoordinatorAssemblyStore`, `HeartbeatStatusStore`, and `RunWatchLoopService` leader election.


## 2026-06-28: Copilot auth blocker / OAuth scope limit

Auth finding: Agentweaver's custom GitHub App OAuth client (`Iv23lieRvX4I63VNekKS`) requests only `repo read:user read:org`; GitHub only issues Copilot-entitled tokens to blessed Copilot clients. User-scoped token lookup remains useful, but re-auth through this app cannot create a Copilot SDK-capable token. Recommended path: Microsoft Foundry.
