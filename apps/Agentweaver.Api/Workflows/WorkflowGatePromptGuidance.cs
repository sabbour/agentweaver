namespace Agentweaver.Api.Workflows;

/// <summary>Shared prompt text for authored workflow gates so workflow and blueprint generation stay aligned.</summary>
internal static class WorkflowGatePromptGuidance
{
    public const string SoftwareBuildTestRequirement = """
        MANDATORY BUILD & TEST STEP (software workflows): For any software-oriented workflow — one that
        implements, fixes, refactors, or otherwise changes code (bug fix, feature delivery, refactor,
        etc.) — you MUST include a build_test gate after any RAI safety check and IMMEDIATELY before the human-review gate. This gate
        is static, platform-owned, and always-on; never omit it, never make it optional, and never add
        an inline prompt. Wire it exactly as:
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
        Route its verdicts: `when: approved` advances to the human-review gate; `when: request-changes`
        loops back to the implementation node (e.g. implement/fix); `when: declined` goes to a terminal.
        If a software workflow has no human-review gate, add one (a `check` node with
        `gate_kind: human-review`) placed immediately after build-test. The build & test gate must run
        after the RAI safety check whenever an RAI gate is present; never place RAI after build_test.
        Consider adding `rai` before build_test for safety-sensitive work and `rubberduck` before
        build_test for code-quality critique. Non-software
        workflows (pure content authoring, discovery, incident response, evaluation) do NOT need this step.
        """;

    public const string BlueprintGateAwareness = """
        GATE-AWARE WORKFLOW SELECTION — blueprints must preserve or trigger specialized gates:
        - `build_test` is the platform-owned Build & Test gate that also lights up preview. For any
          blueprint whose deliverable is buildable/runnable software — app, service, library, feature,
          bug fix, refactor, or other code change — the selected/generated workflow MUST include the
          mandatory build_test gate after any RAI safety check and immediately before human review.
        - `rai` is a `check` gate_kind for responsible-AI safety review. Include it for safety-sensitive
          work, user-facing content, policy/compliance-sensitive decisions, or workflows that could affect
          users if the output is unsafe.
        - `rubberduck` is a `check` gate_kind for AI critique and code-quality review. Consider it for
          code-producing work or complex technical artifacts that benefit from critique before sign-off.
        - `human-review` is a `check` gate_kind for human HITL sign-off. Include it for anything shipping
          artifacts, publishing user-facing output, or otherwise needing accountable approval.
        - Do NOT author or request `merge` or `scribe` gates in generated workflows; the coordinator appends
          that hardcoded tail after authored workflow gates.
        - Ensure blueprint validation/selection never strips, ignores, or downranks authored gates from a
          chosen catalog workflow or a generated workflow.

        When a blueprint's outcome warrants gates and the library fit is weak or generic, PREFER [] (generate)
        so the gate-aware workflow generator can produce a specialized, properly-gated workflow. Do NOT select
        a generic ungated catalog workflow for software delivery, safety-sensitive/user-facing content, or
        sign-off-bound artifact shipping merely because its name or output artifact sounds close.
        """;
}
