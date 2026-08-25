
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


## 2026-07-13T23:59:00-07:00 — Priority-1 E2E
The FitTrack-style priority-1 complex E2E rerun was still progressing through the full lifecycle toward a preview URL at handoff.

## 2026-07-14T02:35:00-07:00 — Batch merge: v0.9.47-rc1 live E2E validation, #269/#270 build/test gates
Scribe merged inbox notes: FitTrackE2E v10 historical baseline and v11 final build/test gate passed for #269/#270; v0.9.47-rc1 live E2E validation completed for #269/#270 run commands; #258 PID identity guard and Linux /proc E2E coverage added; #253/#257/#260 fixes and reviews landed across revisions.

## 2026-07-14T10:15:00-07:00
Reproduced FitTrack priority-1 wedge scenario end-to-end; failure evidence captured for triage/fix assignment.

## 2026-07-14T15:15:00Z — LinkVault stall finding + HabitLoop success
Smith's LinkVaultE2E-v1 run produced enough evidence to file #317 around child `agent_stall_timeout` after real work completed, while HabitLoopE2E-v1 became the first full lifecycle success of the session and reached a live preview plus final completion.

- 2026-07-29: Tank's Entra-first design settled the core shape for QA planning (single-tenant Entra login, Tier-1 platform roles, Tier-2 project RBAC, GitHub as linked secondary capability, explicit relink migration). Your remaining endpoint/precedence/revocation questions stay open follow-ups.


## 2026-07-29 — Issue #641 QA test plan: event trigger predicates, webhook provisioning, and NL generation

Read-only plan based on issue #641 and its comments only. Use this as the acceptance/reviewer matrix for API, UI, OAuth, webhook receiver, and workflow-generation changes.

### Suggested fixtures / evidence
- Repo A connected to Agentweaver and Repo B unconfigured.
- Workflows covering each curated event, including one `event` trigger with no `if:`.
- Payload variants: valid signed JSON, duplicate delivery ids, malformed JSON, oversized body, unexpected review state, empty/huge label arrays, exact vs near-match refs, slash-command and long non-match comments.
- Evidence surfaces to inspect during execution: API logs, telemetry, stored YAML, created webhook config, backlog tasks, and any model prompt/validation traces.

## 2026-07-14T15:15:00Z — LinkVault stall finding + HabitLoop success
Smith's LinkVaultE2E-v1 run produced enough evidence to file #317 around child `agent_stall_timeout` after real work completed, while HabitLoopE2E-v1 became the first full lifecycle success of the session and reached a live preview plus final completion.

- 2026-07-29: Tank's Entra-first design settled the core shape for QA planning (single-tenant Entra login, Tier-1 platform roles, Tier-2 project RBAC, GitHub as linked secondary capability, explicit relink migration). Your remaining endpoint/precedence/revocation questions stay open follow-ups.


## 2026-07-29 — Issue #641 QA test plan: event trigger predicates, webhook provisioning, and NL generation

Read-only plan based on issue #641 and its comments only. Use this as the acceptance/reviewer matrix for API, UI, OAuth, webhook receiver, and workflow-generation changes.

### Suggested fixtures / evidence
- Repo A connected to Agentweaver and Repo B unconfigured.
- Workflows covering each curated event, including one `event` trigger with no `if:`.
- Payload variants: valid signed JSON, duplicate delivery ids, malformed JSON, oversized body, unexpected review state, empty/huge label arrays, exact vs near-match refs, slash-command and long non-match comments.
- Evidence surfaces to inspect during execution: API logs, telemetry, stored YAML, created webhook config, backlog tasks, and any model prompt/validation traces.

### 1) Predicate evaluation correctness

| ID | Scenario | Expected result |
| --- | --- | --- |
| PRED-01 | Matching `event_name` with no `if:` block. | The workflow fires unconditionally, preserving today's behavior for existing Event triggers. |
| PRED-02 | Two sibling conditions in `if:` are both true (for example two `hasLabel` clauses). | The workflow fires only when all sibling conditions are true (implicit AND across the array). |
| PRED-03 | One sibling condition is false while the rest are true. | The workflow does not fire because a single false sibling makes the AND-array false. |
| PRED-04 | `or:` wrapper with one true branch and one false branch. | The workflow fires because `or:` evaluates true when any child condition is true. |
| PRED-05 | `or:` wrapper with all branches false. | The workflow does not fire because `or:` evaluates false when every child is false. |
| PRED-06 | `not:` wrapper around a true child condition. | The workflow does not fire because `not:` inverts the child result. |
| PRED-07 | Nested combination such as `not: { or: [...] }` or `or: [not: ..., ...]`. | Nested boolean wrappers evaluate exactly according to YAML structure, with no flattening or precedence surprises. |
| PRED-08 | Existing YAML containing an Event trigger but no predicates is opened in the UI and saved again. | The trigger round-trips unchanged and is not rewritten into an empty or semantically different predicate block. |
| PRED-09 | A predicate unsupported for the selected event type is authored via UI or hand-edited YAML. | Validation fails visibly and the invalid trigger is not saved or executed. |

### 2) Per-predicate edge cases and security

| ID | Scenario | Expected result |
| --- | --- | --- |
| LBL-01 | `hasLabel` is evaluated against an empty label array. | The predicate evaluates false and the workflow does not fire. |
| LBL-02 | `isNotLabeledWith` is evaluated against an empty label array. | The predicate evaluates true because the target label is absent. |
| LBL-03 | `hasLabel` / `isNotLabeledWith` are evaluated against a very large label list. | Evaluation stays correct and completes within an acceptable bound without timeouts or quadratic slowdown. |
| LBL-04 | Label names differ only by case between config and payload. | Matching follows one documented normalization rule consistently across UI, YAML validation, and runtime evaluation. |
| BR-01 | `baseBranch` is configured for `main` and the PR targets a different branch. | Only the documented exact base branch matches; near-matches do not fire accidentally. |
| REF-01 | `ref` is configured to match `refs/heads/main` exactly and the payload ref is `refs/heads/main-2`. | Exact-match mode does not treat `main-2` as `main`. |
| REF-02 | `ref` is configured in prefix mode for a branch namespace. | Prefix mode matches only the intended namespace and does not over-match unrelated refs. |
| REV-01 | `reviewState` receives an unexpected or newly introduced GitHub review state. | The unknown state matches none of the known values, does not crash evaluation, and does not silently coerce to another state. |
| CAT-01 | `category` is configured for a discussion category not present on the payload. | The predicate evaluates false and the workflow does not fire. |
| CMT-01 | `commentMatches` uses a fixed regex that should exactly match `/agentweaver:triage`. | Only the intended exact comment fires; partial or near matches do not. |
| CMT-02 | `commentMatches` is given a comment with extra trailing text or arguments. | No capture-group or semantic parsing occurs in v1; the result is boolean match/no-match only. |
| CMT-03 | `commentMatches` is configured with a catastrophically backtracking pattern and evaluated against a long hostile comment. | The unsafe pattern is rejected or safely time-bounded so webhook processing cannot be stalled by ReDoS. |
| CMT-04 | A matching and a non-matching comment pass through evaluation while observability is inspected. | The raw comment body never appears in logs, telemetry, stored workflow state, or prompts; only the boolean match outcome escapes. |

### 3) Webhook delivery and receiver resilience

| ID | Scenario | Expected result |
| --- | --- | --- |
| WEB-01 | GitHub retries the same delivery with the same `X-GitHub-Delivery` and event name. | At most one backlog task is created per workflow/event because existing idempotency still holds with predicates enabled. |
| WEB-02 | The same delivery id yields both `github.<event>` and `github.<event>.<action>` matches for different workflows. | Dedupe remains scoped per distinct event name so each intended workflow fires at most once. |
| WEB-03 | A valid delivery arrives for a repository that is not the project's configured repo. | The request is ignored safely and no workflow fires. |
| WEB-04 | A valid delivery arrives for an event that the project has no matching trigger for. | The request completes without error and no workflow fires. |
| WEB-05 | The payload is signed correctly but contains malformed JSON. | The receiver rejects it cleanly, creates no tasks, and does not crash. |
| WEB-06 | The payload is valid JSON but oversized. | The receiver rejects or truncates safely according to server limits, creates no tasks, and remains responsive. |
| WEB-07 | The signature is missing or invalid. | The receiver rejects the delivery and no predicate evaluation or workflow firing occurs. |
| WEB-08 | The project is inactive or its webhook secret is unavailable. | The receiver returns the existing safe failure/no-op behavior and never fires a workflow. |

### 4) Automatic webhook provisioning

| ID | Scenario | Expected result |
| --- | --- | --- |
| AUTO-01 | User clicks **Create webhook automatically**, grants incremental `write:repo_hook`, and GitHub hook creation succeeds. | The hook is created for the selected event set and the UI reports success without breaking the manual path. |
| AUTO-02 | User denies the incremental scope request. | Manual setup instructions remain visible and actionable immediately; the user is never left at a dead end. |
| AUTO-03 | User cancels/closes the OAuth consent flow. | Manual setup instructions remain visible and actionable immediately; the user is never left at a dead end. |
| AUTO-04 | The token already has broader scopes than `write:repo_hook`. | Auto-create reuses the existing authorization and does not force an unnecessary re-consent loop. |
| AUTO-05 | GitHub returns a rate-limit failure while creating the hook. | The UI surfaces a clear failure and still leaves the manual setup path available in the same view. |
| AUTO-06 | Repository permissions change mid-flow and GitHub returns forbidden/not found. | The UI surfaces the API failure clearly and still leaves the manual setup path available in the same view. |
| AUTO-07 | A logically equivalent webhook already exists on the repository. | The flow handles the duplicate deterministically (reuse/report it or fail clearly) without creating an invisible second hook. |
| AUTO-08 | Selected triggers correspond to only a subset of curated GitHub events. | The created GitHub webhook subscribes only to the intended event set, not the full event catalog. |
| AUTO-09 | Any non-granted or failed auto-create path is exercised end-to-end. | The fallback instructions still show payload URL, secret guidance, and content-type requirements with no UI state loss. |

### 5) LLM-based natural-language trigger generation

| ID | Scenario | Expected result |
| --- | --- | --- |
| GEN-01 | Create-mode prompt: “trigger this whenever it's labeled `agentweaver:triage` AND `needs triage`”. | The generated YAML uses `type: event`, the curated GitHub event, and two sibling `hasLabel` predicates representing AND semantics. |
| GEN-02 | Create-mode prompt: “run this every Monday at 9am UTC”. | The generated YAML uses `type: schedule`, `interval: weekly`, `day_of_week: monday`, and `time_of_day: 09:00`. |
| GEN-03 | Create-mode prompt: “whenever someone comments `/agentweaver:triage`”. | The generated YAML uses `type: event` with `commentMatches: { pattern: "^/agentweaver:triage$" }`. |
| GEN-04 | Edit-mode prompt asks to add a trigger to an existing workflow. | The generator preserves unrelated workflow structure and adds only the requested trigger change. |
| GEN-05 | The model first emits a malformed trigger (bad event name, bad predicate shape, invalid YAML). | The correction pass either repairs it into a valid trigger or the operation fails closed; a broken trigger is never silently saved. |
| GEN-06 | Prompt is ambiguous, e.g. “trigger this sometimes”. | No bogus trigger is invented; the result is an explicit failure/ambiguity outcome rather than an unsafe guess. |
| GEN-07 | The model invents an unsupported event or predicate outside the curated vocabulary. | Validation rejects it and the final saved workflow contains no unsupported trigger content. |
| GEN-08 | The natural-language description includes prompt-injection text asking for unsupported trigger fields or unsafe behavior. | The fenced description is treated as data, and the output still obeys the documented trigger schema and validation rules. |

### 6) UI authoring and round-trip behavior

| ID | Scenario | Expected result |
| --- | --- | --- |
| UI-01 | Open the Event-trigger picker in the workflow authoring UI. | Only the curated shortlist (`issues`, `issue_comment`, `pull_request`, `pull_request_review`, `push`, `release`, `discussion`) is exposed. |
| UI-02 | Change the selected event type and inspect available condition rows. | The builder offers only predicates valid for that event type and prevents invalid combinations. |
| UI-03 | Configure an Event trigger with no predicates through the UI. | The saved YAML contains no synthetic empty `if:` wrapper and still behaves as unconditional event matching. |
| UI-04 | Build a simple AND case in the row builder, save to YAML, then reload the editor. | The UI and YAML round-trip without losing rows, changing order, or changing semantics. |
| UI-05 | Author a nested `or:` / `not:` expression in YAML, then reload the builder/editor. | The expression round-trips without semantic drift, silent simplification, or wrapper loss. |
| UI-06 | Load malformed or unsupported hand-edited trigger YAML into the editor. | The UI surfaces a clear validation problem and never silently rewrites it into different logic. |
| UI-07 | View the webhook setup area before and after trying auto-create. | The **Create webhook automatically** button and manual instructions are visible together at all times. |
| UI-08 | Exercise OAuth denial/cancel and hook-creation failure states from the same screen. | Payload URL, secret guidance, and manual setup steps remain visible and usable through every failure state. |

### Highest-risk release blockers
- ReDoS or privacy leakage in `commentMatches`.
- YAML/UI round-trip changing boolean trigger semantics.
- Auto-create OAuth or hook-creation failures leaving the user without a manual fallback.
- Predicate-aware webhook handling regressing existing idempotency/dedupe behavior.

- 2026-08-14: API harness since-0.16 seam PASS on v0.18.1, with PR #766 `selectedAccount` preservation and Edge Default + CDP staging auth captured.
