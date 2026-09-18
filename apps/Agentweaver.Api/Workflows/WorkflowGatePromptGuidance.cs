namespace Agentweaver.Api.Workflows;

/// <summary>Shared prompt text for authored workflow gates so workflow and blueprint generation stay aligned.</summary>
internal static class WorkflowGatePromptGuidance
{
    public const string SoftwareBuildTestRequirement = """
        MANDATORY BUILD & TEST STEP (software workflows): For any software-oriented workflow — one that
        implements, fixes, refactors, or otherwise changes code (bug fix, feature delivery, refactor,
        etc.) — you MUST include exactly one build_test gate immediately after any RAI safety check,
        followed immediately by exactly one human-review check gate. Every reachable RAI safety gate's
        approved or pass edge MUST route directly to that build_test gate; no path may reach human review
        before this RAI/build_test sequence. Neither gate is optional or omittable. The build_test gate is
        static, platform-owned, and always-on; never add an inline
        prompt. Wire it exactly as:
          - id: build-test
            type: build_test
            label: Build & Test
            role: review
            agent: qa-engineer
        Route `when: approved` directly to the human-review gate; `when: request-changes` loops back to
        the implementation node (e.g. implement/fix); `when: declined` goes to a terminal. The
        human-review gate MUST be `type: check` with `gate_kind: human-review` and branches `approved`,
        `request-changes`, and `declined`; route its approved verdict to the workflow's appropriate
        terminal/action, its request-changes verdict to implementation, and its declined verdict to a
        terminal. Consider `rai` for safety-sensitive work and `rubberduck` for code-quality critique.
        Non-software workflows (pure content authoring, discovery, incident response, evaluation) do NOT
        need build_test.
        """;

    public const string ContentOnlyExemption = """
        CONTENT-ONLY WORKFLOW: This request is explicitly limited to pure content authoring. Do not add
        software delivery gates such as build_test. Preserve any requested safety and human-review gates.
        """;

    public const string BlueprintGateAwareness = """
        GATE-AWARE WORKFLOW SELECTION — blueprints must preserve or trigger specialized gates:
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
