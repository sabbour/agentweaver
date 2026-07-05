# Decision: Blueprint workflow matching vs workflow generation — 2026-07-05

**Status**: DECIDED (bug — fix implemented)
**By**: Morpheus (Runtime Engineer)
**Refs**: GitHub issue #176 (sabbour/agentweaver)
**Repro**: `/projects/da60d9ae-3185-4747-8729-92b164bbf60d/orchestrations/799dd3c6-9b19-4f61-ad4b-ba6eb22cad07`

## Context

Prompt under investigation:

> "GitHub issue triage. Look at open issues in a GitHub repo (https://github.com/Azure/aks)
> that are not features yet, deduplicate, identify customer pain points, do research and
> validation, then write a PRD."

During blueprint generation the library-first matcher selected the generic **Product Management
Discovery** workflow (`pm-discovery`). Generating a workflow from the same prompt produced a much
better specialized topology (triage -> dedupe -> research/validate -> PRD). The question was whether
the generic match was correct, and why the two paths diverge.

## Paths involved

- Blueprint generation (project creation): `CopilotBlueprintGenerator.GenerateRawAsync` performs
  library-first workflow matching. It returns `[]` when no library workflow fits; `BlueprintService.GenerateAsync`
  then invokes `CopilotWorkflowGenerator` (the fallback) to author a bespoke workflow.
- Coordinator run-time selection: `WorkflowSelector.SelectAsync` picks among the project's
  `allowed_workflow_ids`. It cannot generate; when nothing fits it falls back to the project default.
- Workflow generation: `CopilotWorkflowGenerator.GenerateAsync` decomposes the described process into
  typed nodes/edges. It has no library to snap to, so each described stage becomes a node.

## Findings (answers to the three questions)

1. **Was matching `pm-discovery` correct?** No. `pm-discovery` is `research -> synthesis ->
   stakeholder review -> scribe`. The prompt's process has distinctive **triage**, **deduplication**,
   and **research/validation** stages that `pm-discovery` does not model. Matching it drops those
   stages. The correct behavior is to return `[]` from the library matcher so a specialized
   triage -> dedupe -> research -> PRD workflow is generated. This is under-selection — a bug.

2. **Why does generation beat matching, and are the criteria divergent?** Yes, divergent.
   - The matcher answers a coarse "does an existing library workflow's process fit?" over ~6 generic
     workflows. Its guidance warned only against **name / domain-word** similarity ("planning" in
     both name and domain; travel-vs-PM). It said nothing about **output-artifact overlap** — and the
     issue prompt trips exactly that trap: `pm-discovery`'s description ("Outputs are documents and
     specs, PRDs") shares its deliverable (a PRD) with the prompt, so the model read artifact overlap
     as process fit even though the stages only partially cover the task.
   - The generator has no matching bar at all: its job is topology synthesis, so it decomposes each
     described step (triage, dedupe, pain-point ID, research/validate, PRD) into a distinct node.
   - The two matchers also disagreed on the no-fit terminal rule. Blueprint matcher: "return an empty
     array [] ... better than a wrong selection" (-> generate). Run-time selector: "If no workflow is
     a good process fit, select the first listed workflow (the project default) instead of guessing"
     (-> generic default). The selector's rule is correct **for its stage** (it cannot generate), but
     both matchers must reject the same false signals (name, domain-word, and output-artifact overlap).

3. **Should blueprint prefer generating over matching on weak fit?** Yes. When a library workflow only
   partially covers the described distinctive stages, the blueprint step must return `[]` and let the
   generator author a specialized workflow. Generated workflows are validated (`WorkflowDefinitionLoader`
   + `RunWorkflowGraphBinder.ValidateBindable`), materialized, and immediately runnable, so preferring
   generation on weak fit yields a better-fitting topology at no correctness cost. Strong / full-coverage
   matches should still prefer the library workflow (pre-built, reviewed, cheaper).

## Root cause

The blueprint library-first matcher treated **output-artifact overlap** (both produce a PRD/spec) as
process fit, and had no explicit **full-coverage** requirement. Its fit bar was permissive enough that a
partially-covering generic workflow (`pm-discovery`) suppressed the superior generator, which only fires
when the matcher returns `[]`.

## Resolution

Reconcile the matcher criteria with the generator's intent, in the prompts:

- `CopilotBlueprintGenerator` workflow-selection guidance now states: output-artifact overlap is NOT
  process fit; a library workflow fits ONLY if its stages cover ALL the distinctive stages of the
  described process (a FULL-COVERAGE test); partial coverage is under-selection and MUST return `[]`;
  when in doubt between a partial match and generating, PREFER `[]`. Adds the concrete
  "triage -> dedupe -> research -> PRD is NOT Product Management Discovery" example mirroring #176.
- `WorkflowSelector.BuildPrompt` gains the matching consistency rule: producing the same KIND of output
  artifact is not process fit; a workflow fits only if its stages cover the distinctive stages of the
  task. (The selector still falls back to the project default when nothing fits — correct, since it
  cannot generate.)

Deterministic tests capture the case (the paths run a live LLM, so we assert the reconciled criteria are
present in the grounded prompts):
- `CopilotBlueprintGeneratorTests.GenerateRawAsync_WorkflowSelection_RejectsOutputArtifactOverlap_AndPrefersGeneratingOnPartialFit`
- `WorkflowSelectorTests.MultiWorkflow_CallsLlm_ParsesAndValidatesSelection` (extended)

## Expected behavior for this class of prompt

A "triage -> dedupe -> research/validate -> PRD" (or any multi-stage operational) prompt whose distinctive
stages are not fully covered by a library workflow must generate a specialized workflow, not match a
generic one. Library matching is reserved for prompts whose process is fully covered by an existing
workflow.
