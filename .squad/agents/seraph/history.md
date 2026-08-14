# Seraph — History (Summarized)

## 2026-07-29 — Entra-first RBAC and issue #641 hardening
- Tank's Entra-first design fixed the security direction: single-tenant Entra login, Tier-1 app roles, Tier-2 project RBAC, GitHub as linked capability, and hard server-side authorization before linked-token use.
- Reviewed issue #641's event-trigger security design and required boolean-only, ReDoS-safe comment matching, raw-comment redaction, and explicit incremental webhook consent with a safe fallback.
- Wrote the QA matrix for issue #641 covering predicates, webhook resilience, auto-provisioning, natural-language trigger generation, and UI round-trip behavior.
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

## 2026-07-05T13:17:12-07:00 — Security approval for tool approval expiry

Seraph approved PR #182 / issue #174. Security finding: the backend remains fail-closed, timeout behavior is preserved, and using child run id plus full request id does not introduce IDOR. No merge yet; coordinator validates on staging first.

## 2026-07-05T14:16:02-07:00 — Issue #183 security approval
Seraph approved PR #184 for #183. The workflow-selection change reduced attack surface by making the classification turn tool-less while leaving runtime tool approval gates and sandbox behavior untouched.


## 2026-07-05T20:40:00-07:00 — v0.7.11 release batch
Removed project relink end-to-end to close arbitrary server-path exposure: frontend, REST endpoint, DTO, service/store mutation, MCP tool, and settings UI. Initial working-directory assignment remains only in create/import flows.


## 2026-07-06 v0.9.0 staging wave
- Re-reviewed the skills-import SSRF fix and cleared it green for release.
📌 Team update (2026-07-10T05:55:00-07:00): #207 redesign remains YELLOW until fenced claims, tenant-scoped capabilities, fail-closed remote execution, fair bounded recovery, deletion cancellation/idempotency, audit redaction, and mTLS are mandatory. — decided by Seraph

## 2026-07-14T02:35:00-07:00 — Batch merge: triage batch (#216/#224/#226/#227/#266), #253/#255 reviews
Scribe merged inbox notes: staleness re-verification triage across #216 (still open, run/always approvals remain tool-scoped), #224 (still open, no separate agent scratch root), #226 (stale human-review gate steering now drained), #227 (still open, non-pending review delivery superseded), #266 (partially fixed, /proc observation exists). #253/#255 review rounds approved/rejected across revisions; #258 rev5 and #264 v2/v3 reviews completed.

## 2026-07-14T03:05:00-07:00 — Continuous triage pass 2, #307 tagged-release confirmation
Ran triage pass #2 and a dedicated tagged-release check for #307: confirmed the AgentHost pod right-sizing fix is live on 0.9.49-rc1 (not an ad hoc manifest), with a real triggered run observed autoscaling out correctly under scheduling pressure and zero evictions/OOM kills. Evidence posted to #307; recommend closing with both load-test and tagged-release evidence.

## 2026-07-14T10:15:00-07:00
Continuous triage pass #2 findings folded into decisions.md this batch (see #216/#227 validation notes from prior pass carried forward as context).


## 2026-07-14T10:15:00-07:00 (late arrival)
Triage pass #3: backlog 55 open (down from 67). Filed #314 (#309 follow-up). Flagged process finding re: #312/#247 closed on review-tests-only while fixes remain uncommitted diffs — recommended reopen or new closure tier, no unilateral action.


## 2026-07-14T11:05:00-07:00
Coordinator acted on the pass-3 closure-discipline finding: #311/#208/#312/#247/#310 reopened. Vindicates the process flag raised without unilateral action.

## 2026-07-14T15:15:00Z — Release-readiness + pass-4 triage folded into ship
Seraph's release compile and triage pass #4 became part of the v0.9.50-rc1 ship record: #314 was treated as a P2 bundle item, while #97/#108/#200/#246/#271 stayed explicitly open as architecture-level work.


## 2026-07-14 Session: Pre-Implementation Security Review (3-Harness Design)

**Review Scope:** docs/api/ui/mcp-test-harness-plan.md, approval-driving implementation (b4ac1104), .squad/ceremonies.md harness-related sections.

**Verdict:** 2 🔴 blocking findings (design-level, non-large, pre-req for build start); 3 🟡 advisory findings (fold in during implementation).

**Blocking Findings:** (1) No hard target-host allowlist — approval-gate execution has zero host check; suggested fix: mandatory allowlist at client construction. (2) No prompt-injection threat model — LLM input built from live content (MCP tool descriptions, DOM, API errors); suggested fix: untrusted-content delimiters + system instruction + defense-in-depth for approval-gate mutations + test scenario.

**Positive Findings:** approval-judge.mjs deny-by-default posture correct; credential hygiene notes correct; checkInsecureAllowed good prior art; Harness GitHub-authority boundary enforced by code (no mutation paths).

**Next:** Await Tank/Trinity/Coordinator coordination to address findings in specs before implementation phase begins.


---

## 2026-07-14: Fleet-Mode Harness Build Wave — Security Review & Blocking Findings Resolution

**Wave:** Full fleet-mode harness infrastructure implementation (API/UI/MCP + shared + security review)

**Contribution:** Completed comprehensive pre-implementation security review of three-harness fleet architecture. Identified 5 major findings: (1) Sandbox/approval-driving (🔴 BLOCKING) — no deterministic policy-enforcement layer; (2) Credential handling (🟡 Advisory) — ambient token exposure risk; (3) Prompt-injection surface (🔴 BLOCKING) — untrusted tool descriptions + API responses fed to LLM without boundary; (4) Squad↔Harness trust boundary (🟡 Advisory) — no provenance validation before issue actions; (5) Governance/authority expansion (🔴 BLOCKING) — LLM agent with latent GitHub authority.

**Outcome:** All 5 findings documented with required fixes. Findings 1, 3, 5 RESOLVED in design by Tank (API), Trinity (UI), Morpheus (MCP) in lockstep: target-guard.mjs mandatory allowlist (staging/localhost only, `--allow-prod` + confirmation escape hatch); untrusted-data delimiters in all prompts; judge NOT sole authority with in-scope downgrade-to-defer validation; Harness agent with zero GitHub tools/credentials + no permission to modify scope. Findings 2, 4 documented as advisory with implementation deferred to security-hardening phase.

**Coordination:** Direct feedback to Tank/Trinity/Morpheus for lockstep spec fold-ins. Ahmed's sandbox-architecture clarification narrowed Finding 1 from "deny all tool/shell approvals" to "allow sandboxed approvals but enforce deployment allowlist" — aligns with Agentweaver's own Kubernetes isolation.

**Follow-ups:** Append-only audit trail integration. Hostile-content self-test scenarios (approval injection, scope escalation, GitHub access attempts). Credential isolation architectural review. Live-staging E2E validation of all guardrails + injection resistance.

- 2026-07-29: Tank's Entra-first design adopted your hard requirements as non-optional constraints: server-side app-role enforcement, owner/project authorization before linked-token resolution, and a hard cutover away from legacy GitHub bearer auth.

## 2026-07-29T22:46:49+03:00 — Issue #641 event-trigger security design review
Reviewed the design for structured GitHub event-trigger predicates, `commentMatches`, webhook auto-provisioning, and NL trigger generation. Confirmed the current trust boundary in `GitHubWebhookPayload.cs`, `WorkflowEventTriggerService.cs`, and `GitHubWebhookEndpoints.cs`: raw webhook body text is not routed into workflow firing today, and HMAC verification occurs before JSON parsing/dispatch. Posted issue comment with verdicts: `commentMatches` privacy boundary 🟡 (must remain boolean-only, no logs/persistence/prompts), ReDoS safety 🔴 (safe engine/non-backtracking subset + hard timeout required before ship), incremental `write:repo_hook` scope upgrade 🟡 (explicit click-only, request-scoped, graceful denial fallback), HMAC ordering 🟢, curated vocabulary 🟢. Recorded hard blockers in decision inbox via `Seraph-issue-641-security-blockers-keep-comment-text-priv.md`.
