# NFR-003 Pre-Release Bias and Fairness Review

**Document:** Bias Review — Single-Agent File-Editing Run
**Status:** Complete
**Reviewer:** Rai (AI Responsible reviewer)
**Date:** 2026-06-07
**Spec:** specs/001-single-agent-run/spec.md

---

## Purpose

This document satisfies NFR-003: All AI-influencing defaults must be reviewed
for fairness and bias concerns before release. This review gates the release
of the single-agent run feature.

---

## Scope of Review

### 1. Default Task Prompt Preamble

**Item:** No system-level preamble is injected into the agent loop at this time.
The agent receives only the user-supplied `taskPrompt` verbatim.

**Concern:** If a default preamble were added later, it could introduce framing
bias that subtly influences the agent's output quality or content depending on
the author's cultural or linguistic assumptions.

**Finding:** No bias introduced. The absence of a default preamble is the
correct behavior. The agent operates on the raw user intent.

**Mitigation:** Review any future system preamble additions against this checklist
before merging.

---

### 2. Tool Allowlist Defaults

**Item:** The tool allowlist contains exactly `read_file` and `write_file`.
This is enforced by `GovernancePolicyEngine.IsToolAllowed()`.

**Concern:** A too-broad allowlist could enable agentic actions with unequal
impact across workspaces (e.g., deleting files a user cannot recover). A
too-narrow allowlist could create unequal usability for users with legitimate
needs.

**Finding:** The two-tool allowlist is the most restrictive defensible scope
for a file-editing agent. It does not discriminate by user, language, file type,
or content. No bias identified.

**Mitigation:** Expand the allowlist only through explicit governance review.
Tool additions must include a new bias review entry.

---

### 3. Model Source Defaults

**Item:** The `modelSource` field is required per run; there is no server-side
default that silently selects a provider. Both `CopilotSdk` and `MicrosoftFoundry`
are treated as equally valid by `GovernancePolicyEngine.ValidateModelSource()`.

**Concern:** Implicit provider selection could create unequal outcomes if one
provider produces outputs with systematic demographic or linguistic biases.

**Finding:** No server-side default model source is applied. The caller must
explicitly select a provider. This is the correct design — it surfaces the choice
to the human operator rather than hiding it.

**Concern (secondary):** The provider adapters themselves (CopilotSdkAdapter,
MicrosoftFoundryAdapter) are stubs. When real model API calls are wired:
- The underlying models must be evaluated for demographic and linguistic bias.
- Content-safety checks (`ContentSafetyInterceptor`) must be calibrated to
  apply equally across all language communities, not just English.

**Mitigation:** Before activating real model API calls, run model fairness
evaluations per each provider's published responsible AI documentation.
Wire the content-safety API with provider-appropriate thresholds reviewed by Rai.

---

### 4. System Prompt Injected into the Agent Loop

**Item:** The current `AgentLoopHost` (stub) does not inject a system prompt.
Real model calls should be configured with a neutral, task-focused system prompt.

**Concern:** A system prompt that assumes a particular coding style, locale, or
cultural context could produce outputs biased toward certain developer communities.

**Finding:** No system prompt is currently injected (stub implementation).

**Mitigation (required before real model wiring):**
- Compose a minimal system prompt that:
  - Describes the agent's role without cultural framing.
  - Does not assume any particular programming paradigm, naming convention, or
    project structure.
  - Is reviewed by Rai and a diverse set of stakeholders before deployment.
- The system prompt must not contain language that could produce disparate output
  quality across languages, coding styles, or geographic regions.

---

### 5. Run Limits (maxSteps, maxDurationSeconds)

**Item:** Default values: `DefaultMaxSteps = 200`, `DefaultMaxDurationSeconds = 1800`.

**Concern:** If default limits are too low, users working with larger codebases
or slower machines may hit them disproportionately, creating unequal outcomes.

**Finding:** The defaults (200 steps, 30 minutes) are generous for a first release.
They apply uniformly to all users with no differentiation by identity.

**Mitigation:** Monitor distribution of `stepCount` in `OperationalRecord` after
launch. If the 95th percentile of step counts is near the default, raise the default
or document that callers should supply explicit values.

---

### 6. SubmittedBy Identity Recording (FR-024)

**Item:** The `submittedBy` field is populated from the `X-Submitted-By` header,
falling back to "anonymous" if not provided.

**Concern:** Anonymizing identity could reduce accountability for abuse; requiring
it could exclude users in environments where identity disclosure is not possible.

**Finding:** The current implementation is a reasonable balance for a first release.
The named field enables audit trails (FR-024) without mandating authentication.

**Mitigation:** Before production deployment, integrate with an authentication system
so `submittedBy` reflects a verified identity. Ensure the authentication system
itself is reviewed for equitable access.

---

## Summary and Release Gate

| Item | Status | Action Required |
|------|--------|-----------------|
| No default task prompt preamble | Pass | None for current release |
| Tool allowlist (read_file, write_file only) | Pass | Review on any expansion |
| Model source: no server default, equal treatment | Pass | Model fairness eval before real API wiring |
| System prompt: none injected (stub) | Conditional Pass | Review required before real model wiring |
| Run limits: uniform defaults | Pass | Monitor step-count distribution post-launch |
| SubmittedBy: header-based with anonymous fallback | Conditional Pass | Authenticate before production |

**Release gate result:** CONDITIONAL PASS

The feature is safe to release as a developer preview with stub model adapters.
Real model API wiring requires:
1. Model fairness evaluations for both CopilotSdk and MicrosoftFoundry.
2. Rai review of any system prompt before injection.
3. Content-safety API calibration for multilingual fairness.

All three items are tracked as pre-GA requirements.
