
## 2026-07-10 — PM & Software Development Blueprint: Staging Test Matrix

Produced the black-box acceptance oracle and coverage matrix for the `blueprint-pm-and-software-development` staging journey. Read-only; no product code or tests modified.

**Deliverable summary:**
- 30 P0 platform-correctness test cases (TC-01–TC-30) covering: project creation, coordinator orchestration, outcome-spec draft/confirm/revise, work-plan dependency ordering, child run dispatch, role handoffs (PM→Engineering), reviewer request-changes + lockout, QA test-gate, RAI check, rubberduck gate, build-test gate, human review gate, preview URL dynamic discovery, audit event monotonic ordering, no-emoji, run bounds, owner scoping, memory/decisions surfaces.
- 7 P1 output-quality observations (TQ-01–TQ-07) including workflow-selection correctness per Decision 007.
- Full per-run evidence checklist (30+ items, including telemetry), unexplained-warning/error definition.
- Clean-run criteria (8 conditions; two consecutive clean runs required before declaring flawless).
- Explicit separation of platform correctness vs. output quality failures.
- 8 items documented as CANNOT_DETERMINE through allowed surfaces (kernel isolation, LLM model, checkpoint backend, KV store, RAI policy specifics, lockout DB record, A2A timing, HPA events).
- 10 conditional regression tests (R-01–R-10): add only after observed defects, not preemptively.
- Seed prompt: "I want to build a personal expense tracker web app. Research what people actually want from expense tracking tools, figure out the key problems users face, design a product plan for it, then build the application."
