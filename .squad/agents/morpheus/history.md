# Morpheus — History (Summarized)

## 2026-06-07 through 2026-06-18 — Early platform delivery

- Built core run/runtime types, sandbox validation/tools/redaction, provider integrations, and the MAF orchestration loop.
- Delivered the team-casting catalog (9 groupings, 31 roles, 14 universe pools) and major Coordinator Phase 1–2 foundations: outcome/work-plan orchestration, dispatch, steering, child runs, recovery, and graph metadata.
- Maintained a clean build/test record across the early 236–430 test suite.

## 2026-06-26 through 2026-06-27 — Sandbox and distributed execution architecture

- Rejected moving workspace creation into the untrusted sandbox pod: it would relocate the CIFS mount bug, broaden the threat model, and add no isolation. Adopted per-project storage/worktree isolation while keeping compute ephemeral.
- Authored the pod-per-run AgentHost architecture: reasoning moves into per-run pods while orchestration/governance remain in API/worker; keys never move with reasoning.
- Spec 018 converged on sandbox-hosted agent turns, durable PostgreSQL state, worker-local orchestration, and checkpoint/release around HITL or coordinator suspension.
- Final transport override selected A2A as the sole worker-to-AgentHost path; rollback is in-process execution, not kube-exec fallback.
- Coordinated the 12-agent deep-dive documentation fleet; all 13 pages completed.

## 2026-06-27 through 2026-06-29 — Credentials, preview, recovery, and usage

- Implemented the shared RWX token-store path for AgentHost: pods read the user's existing token file in place; no token injection or movement. Shipped in commit `37fc1cd`.
- Clarified MXC and the in-cluster agent-sandbox controller as distinct runtimes. Documented browser preview and port-forward behavior. Preview shipped, with cluster-state SandboxClaim resolution fixing multi-replica pod-name lookup.
- Identified the remaining autonomous-demo blocker as Copilot entitlement/model credential selection rather than sandbox or preview infrastructure.
- Root-caused a PostgreSQL 40001 incident to multiple replicas recovering the same orphaned coordinator before any SandboxClaim was created; handed the advisory-lock fix to Tank.
- Delivered Feature 019 turn-level AIC capture plus per-user GitHub token scoping and per-run A2A bearer tokens. Key invariant: usage accumulates per turn, credentials scope at ingress, and bearer-token lifecycle follows pod cleanup.

## 2026-07-05 through 2026-07-10 — Workflow selection and coordinator reliability

- Completed #176 blueprint matching: library matches require full distinctive-stage coverage; partial/output-only overlap falls back to generated workflows. ADR: `.squad/decisions/007-blueprint-match-vs-workflow-gen.md`.
- Owned the initial #183 workflow-selection fix, then entered strict author lockout after review found missing final-message-only SDK capture; Tank completed the revision.
- Contributed to v0.7.11 installation-token identity and stale `assembly_blocked` recovery validation.
- Read-only v0.7.12 forensics found benign live 404s but identified duplicate cross-replica dispatch plus isolated-worktree reads as a separate assembly blocker.
- Shipped live coordinator send routing and server-side Operator direction in the v0.9.0 staging wave.
- Took ownership of the independent #207 redesign after the first process-local approach was rejected. Accepted scope requires remote-only final-Scribe execution, stable identity, durable fencing, bounded retries, exact eligibility, cancellation/deletion invalidation through effect publication, and multi-replica recovery proof.

## 2026-07-12T06:33:29-07:00 — Shipped blueprint catalog evaluation

Evaluated all five shipped blueprints and live default-run behavior. Found moderate-to-high composition redundancy: coordinator decomposition currently supplies lifecycle completeness beyond the selected workflow, so the default blueprint does not encode the advertised lifecycle itself. Proposed a purpose-built lifecycle DAG, eight-role core roster, conditional specialist casting, centralized platform gates, explicit PM/AI workflows, and canonical team-profile derivation. Artifact: `files/eval-shipped-blueprints.md`. Proposal is recorded in `decisions.md` and remains pending coordinator acceptance.


## 2026-07-13T23:59:00-07:00 — #269 Kata passthrough and #305 follow-up
Implemented the #269 conditional AgentHost passthrough override only for in-cluster Kata mode, retaining bubblewrap elsewhere. Build was clean; staging validation remains pending. A follow-up Morpheus task is fixing #305's steering revision-child authoritative-branch mismatch.

## 2026-07-14T02:35:00-07:00 — Batch merge: #269 live E2E validation, #227/#308/#309 steering fixes
Scribe merged inbox notes: #269 Kata bwrap passthrough fix confirmed deployed and live-E2E validated on staging (no bwrap/mount-proc errors, build/test gate passed). #227 race-loser/arm-window redirect at review gate now settles terminal superseded instead of ghost queued. #308 coordinator assembly-recovery wedge and #309 human-steer redirect at parked assembly both fixed.

## 2026-07-14T10:15:00-07:00
Flakiness-recheck pass completed; confirmed BookClub/TrailMix regression tied to #267, evidence used to justify reopening #267.


## 2026-07-14T10:15:00-07:00 (late arrival)
#251 retag-forward residual risk found in the #303 fix; hardened release_ref_for_tag() with linear-ancestry guard + added ACR provenance stamping/verification script. Release-pipeline-critical, flagged for peer review.

## 2026-07-14T15:15:00Z — Release-wave confirmations
Morpheus's #175 investigation confirmed the workflow-save bug was already fixed/live, #240 stayed deferred to the bigger resilience architecture, and the #311 fast-follow consolidation note fed the reserved-role ship record.


## 2026-07-14T10:56:00-07:00 - MCP test harness design spec
Authored docs/mcp-test-harness-plan.md (committed 9dc223a9): a third persona-driven validation harness for Agentweaver's MCP surface (90 tools, RFC-9728 OAuth + GitHub-token passthrough, stdio+streamable-HTTP transports), mirroring the API harness's brief-driven/LLM-in-the-loop/driver-only architecture with MCP tool calls as turns. Investigated the real surface in apps/Agentweaver.Mcp and epic #295 (#128/#129/#130/#131/#201). Cross-Harness Shared Layer recommends ONE shared judge core + thin MCP evidence adapter (option a, justified by cross-surface meta-aggregation and the already-surface-agnostic persona-judge-verdict/v1 schema) and a shared scripts/persona-briefs/ package for all three harnesses. Non-interfering rollout (new scripts/mcp-persona-harness/ sibling; shared-package extraction deferred to a safe checkpoint, no edits to Tank's or Trinity's in-flight files). Decision recorded for reconciliation with Trinity's parallel UI recommendation.


## 2026-07-14 Session: 3-Harness Design Spec (MCP Surface)

**Major Deliverable:** docs/mcp-test-harness-plan.md (full design spec); grounded MCP surface investigation (90 tools / 14 categories, OAuth auth flow, lever mapping vs API harness); recommended shared judge core + thin MCP evidence adapter (agentweaver.mcp-transcript/v1 evidence schema); aligned naming (scripts/mcp-harness/) and shared-layer structure with Tank/Trinity per Coordinator reconciliation.

**Security review:** Participated in Seraph gate; flagged blocking design findings (target-host allowlist, prompt-injection) on all three harnesses equally.

**Next:** Phase 1 parallel work — Morpheus MCP client scaffolding (minimal @modelcontextprotocol/sdk), stdio/http target support; await Tank's Phase 2 checkpoint before shared-package extraction.


---

## 2026-07-14: Fleet-Mode Harness Build Wave — Runtime Fixes & MCP Harness Design

**Wave:** Full fleet-mode harness infrastructure implementation (API/UI/MCP + shared + security review)

**Contribution:** Completed 7 runtime bugfixes (#240, #242, #267, #314, #315, #317, #318) + 4 harness design entries (MCP harness skill structure, live discovery + capability contract, shared judge verdict joins, security guardrails fold-in from Seraph pre-implementation review).

**Outcome:** All runtime fixes have code + regression tests green; E2E staging verification still required on 4 issues. MCP harness ready for live discovery integration + protocol E2E. Seraph security findings 1, 3, 5 folded into MCP spec (target-guard, untrusted delimiters, zero GitHub tools); findings 2, 4 documented as advisory/resolved via versioning.

**Coordination:** Lockstep with Tank (API) and Trinity (UI) on two-file skill structure, target-guard implementation, untrusted-delimiter contract, and verdict schema versioning. Ahmed's sandbox-architecture clarification narrows Finding 1 scope to deployment allowlist, not tool-execution scopes.

**Follow-ups:** Live-staging E2E required on all 4 open runtime issues. MCP protocol end-to-end test + hostile-content injection resistance scenario.


---

## 2026-07-15: #272 Re-fix — Orphaned Outcome-Spec Deferral Drain (reopened after live harness)

**Trigger:** Live API-persona harness re-verified my prior merged #272 fix (v0.9.56 / 864e2c51) against staging and found it does NOT work: `steer kind=send "yes, looks good, please proceed"` returned 201/applied but `coordinator_status` stayed frozen at `awaiting_confirmation`, step_count 0, events frozen at 216. #272 reopened.

**Root cause:** NOT a wiring gap — `steer kind=send` DOES reach `TryHandleOutcomeSpecReplyAsync` -> `ClassifyOutcomeSpecReply` -> `Confirm/ReviseOutcomeSpecAsync` -> `SubmitDecisionAsync`. The real hole: on the pod-per-run deployment the coordinator's reasoning pod is reaped at the `awaiting_confirmation` HITL gate, so the API has NO resident watch loop. When the decision can't apply synchronously (checkpoint-restore race / MAF `$type` bug) `SubmitDecisionAsync` defers it to the `DeferredDecisions` table and returns Accepted. The only drainer (`PollDeferredDecisionsAsync`) lives inside a live watch loop; `CoordinatorReconciler.SweepAsync` only covers work-plan phases; startup recovery only fires on boot. So the pre-work-plan spec gate had no periodic recovery -> deferred confirm/revise orphaned forever. In-process tests passed because in-API mode keeps the run resident (never checkpoints at the gate). Chat AND UI confirm share the hole.

**Fix:** Added `CoordinatorRunService.DrainOrphanedSpecDeferralsAsync`, invoked each tick by `CoordinatorHeartbeatService` (isolated try/catch after the reconciler sweep). Discards stale deferrals for runs no longer at the gate; for eligible checkpointed orphaned spec-gate runs, re-establishes the resident workflow+poller via the proven `RecoverSpecPhaseAsync` seam (applies the decision at-most-once); leaves resident/checkpoint-less runs untouched; retries throttled 15s/run. Original fail-closed-to-revise classification untouched.

**Validation:** Solution builds clean (0/0). All 30 Coordinator Phase2 tests pass incl. 3 new regression tests (deferred-decision applied confirms spec; stale non-gate deferral discarded; empty no-op). Full suite 2238 passed; 6 failures are pre-existing/environmental (Linux real-sandbox + PodLocalWorkspaceManager Windows-path + 1 timing-flaky AsyncStreamIdleTimeout) — confirmed identical on clean baseline main with changes stashed. E2E resume-drain not in-process testable (no checkpoint in in-api mode); validated by composition. Coordinator to re-run live harness on fresh deploy.

**Status:** Committed on `fix/coordinator-steer-confirm-272` (1ca944f4, "Fixes #272"). No PR opened. Decision persisted to inbox. Ready for coordinator merge.

### 2026-07-15 (same day, follow-up): SECOND root cause — brittle affirmation regex

Coordinator traced the classifier while I was on the deferral drain and surfaced a second, independent bug (verified by me). `ClassifyOutcomeSpecReply`'s single fully-anchored `OutcomeSpecAffirmation` regex only allowed a fixed whitelist of follow-words after `yes/ok/sure`; the harness phrase "yes, looks good, please proceed" (normalized `yes looks good please proceed`) failed the whole anchored match because `looks` is not whitelisted -> classified **Revise**, not Confirm. So both bugs stacked: misclassified as Revise, then that decision orphaned by the deferral gap.

Fix: replaced the rigid pattern with a clause-based classifier — split on punctuation/conjunctions and require EVERY clause to be an independent pure affirmation (affirmation-only vocab), keeping the clarification-marker guard. Preserves fail-closed-to-revise. Verified with a 31-case battery (19 confirm/12 revise) and a new endpoint regression test using the exact harness phrase. Committed as 84afd8aa (drain = 1ca944f4). Both commits needed for the harness to pass.
