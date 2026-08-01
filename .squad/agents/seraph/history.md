# Seraph — History (Summarized)

## Through 2026-07-30 — condensed prior context

This file was summarized by Scribe on 2026-07-31T02:54:19.830+03:00 after exceeding 15360 bytes. Preserve durable operating lessons from prior entries: use decisions.md as the active cross-agent decision index; verify live behavior before closing deploy/release work; keep docs aligned to shipped behavior; protect credential boundaries; preserve provider split/migration parity for backend persistence; respect sandbox/published-workload isolation; and treat stale-image/provenance, session-store SQLite locking, Azure Files SQLite locking, and OAuth authorization lessons as standing context.

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

## 2026-07-31T02:54:19.830+03:00 — Cross-agent publishing-apps spec exploration synthesis

- Opus 5 analysis batch supersedes the earlier default-model Link/Seraph/Tank/Trinity run for this topic.
- Four unresolved cross-agent conflicts remain for the spec owner: (1) Link's phase-1 shared `agentweaver-published` namespace versus Seraph's per-project `aw-published-{projectId}` isolation; (2) Link's same-ACR published/* prefix and scope maps versus Seraph's preference for a separate generated-image registry to avoid platform-image pull exposure; (3) whether published apps may reach the Agentweaver API at all — Seraph's phase-1 default-deny/no API path conflicts with Trinity/Tank workflow projection flavor (b), which needs a scoped OAuth client to invoke/read workflow runs; (4) default revision behavior — Tank recommends pinned immutable snapshots, while product may still need an explicit tracked-head/regenerate path.
- Hard blockers: #582 rootless BuildKit must land before phase-1 publish, and WorkflowDefinition lacks declared inputs/outputs so workflow projection apps cannot be schema-driven yet.



2026-07-31T03:40:59+03:00 — Publish-apps exploration completed discussion-only. Seraph's security line remains digest-pinned deploys, project/audience boundaries, no unattended promotion of model-authored code, and scoped API access for published apps.
