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
