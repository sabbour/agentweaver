namespace Agentweaver.Api.Workflows;

/// <summary>
/// The canonical, code-embedded generator for the default run workflow (Feature 010). This is the
/// single source of truth for "the default workflow" — held in code, NOT a checked-in repo file.
///
/// It is materialized per-project at instantiation time into
/// <c>&lt;projectWorkingDir&gt;/.scaffolders/workflows/default.yaml</c> (see
/// <see cref="TryMaterialize"/>), mirroring the Scaffolder template materialization pattern: the user
/// can edit the generated file afterwards. It also doubles as the runtime fallback — a project with no
/// materialized <c>.scaffolders/workflows/</c> (e.g. created before this feature) still resolves this
/// default through <see cref="BuiltInWorkflows"/>, so no migration is required for existing projects.
///
/// The YAML is a behavior-preserving conversion of <c>RunWorkflowFactory.BuildWorkflow</c>: the
/// canonical stage stream (agent, rai, review, merge, scribe) and the same logical nodes, order, and
/// decisions. No emojis appear in any shipped surface (Principle VIII).
/// </summary>
public static class DefaultWorkflowTemplate
{
    /// <summary>The relative path, within a project working directory, where the default is written.</summary>
    public const string RelativeFilePath = ".scaffolders/workflows/default.yaml";

    /// <summary>The canonical default workflow YAML. Parsed through the real loader everywhere it is
    /// used, so it is validated identically to any project-authored workflow (Principle VII).</summary>
    public const string Yaml =
        """
        # Default Run Workflow (Feature 010 — behavior-preserving conversion of
        # RunWorkflowFactory.BuildWorkflow). Generated into each project at instantiation time and
        # editable afterwards. The canonical generator lives in code (DefaultWorkflowTemplate); this
        # file is a materialized copy.
        #
        # It reproduces today's hardcoded run pipeline as a declarative definition: an agent turn
        # produces a change, RAI reviews it (pass through / request a revision up to the iteration cap /
        # fail safe on a content-safety RED), a human-review gate approves / requests changes /
        # declines, an approved change merges, and the scribe records the outcome. The canonical stage
        # stream (agent, rai, review, merge, scribe) and the same logical nodes, order, and decisions
        # are preserved. Plumbing/adapter executors are hidden and not modeled here.

        id: default
        name: Default Run Workflow
        description: Behavior-preserving conversion of the built-in run pipeline (agent, rai, review, merge, scribe).

        trigger:
          type: manual

        start: agent

        nodes:
          - id: agent
            type: prompt
            label: Agent
            role: agent
            kind: live
            prompt: Perform the requested task as an agent turn and produce a change.

          - id: rai
            type: check
            label: Rai
            role: review
            kind: gate
            branches:
              - revise
              - safety-failed
              - no-changes
              - review

          - id: review
            type: check
            label: Review
            role: review
            kind: gate
            branches:
              - approved
              - request-changes
              - declined

          - id: merge
            type: merge
            label: Merge
            role: merge
            kind: action

          - id: scribe
            type: scribe
            label: Scribe
            role: scribe
            kind: agent

          - id: terminal-safety-failed
            type: terminal
            label: Safety failed
            role: plumbing
            kind: terminal

          - id: terminal-declined
            type: terminal
            label: Declined
            role: plumbing
            kind: terminal

          - id: done
            type: terminal
            label: Done
            role: plumbing
            kind: terminal

        edges:
          # Agent turn flows into the RAI gate (unconditional).
          - from: agent
            to: rai

          # RAI verdict routing.
          - from: rai          # revision requested under the iteration cap: loop back to the agent.
            to: agent
            when: revise
          - from: rai          # content-safety RED on an empty diff: fail safe, never reaching review.
            to: terminal-safety-failed
            when: safety-failed
          - from: rai          # no changes produced: skip review/merge straight to the scribe no-op path.
            to: scribe
            when: no-changes
          - from: rai          # otherwise (OK / RED with a diff / revise-at-cap): human review gate.
            to: review
            when: review

          # Human-review gate routing.
          - from: review
            to: merge
            when: approved
          - from: review       # request changes: loop back to the agent with feedback (no cap).
            to: agent
            when: request-changes
          - from: review
            to: terminal-declined
            when: declined

          # Merge outcomes.
          - from: merge        # merged (succeeded or failed terminally): record via the scribe.
            to: scribe
            when: merged
          - from: merge        # blocked: re-enter the review gate via HITL so the run stays alive.
            to: review
            when: blocked

          # Scribe records the outcome and the run terminates.
          - from: scribe
            to: done
        """;

    /// <summary>
    /// Best-effort materialization of the default workflow into a project's working directory at
    /// <see cref="RelativeFilePath"/>. Returns true if the file was written, false if it already
    /// existed (never clobbered) or the write failed. Never throws — project creation must not fail if
    /// this write fails, because the loader regenerates the default from this same template at runtime.
    /// </summary>
    public static bool TryMaterialize(string workingDirectory, out string? error)
    {
        error = null;
        try
        {
            var path = Path.Combine(workingDirectory, ".scaffolders", "workflows", "default.yaml");
            if (File.Exists(path)) return false;

            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, Yaml);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }
}
