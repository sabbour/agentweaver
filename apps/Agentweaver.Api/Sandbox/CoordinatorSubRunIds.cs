namespace Agentweaver.Api.Sandbox;

/// <summary>
/// The coordinator's synthetic sub-run id convention. The coordinator drives its own model turns
/// under ids derived from the parent run id (<c>{parentRunId}-coordinator-decompose</c> and
/// friends). Those ids are NOT rows in the run store and are NOT parseable as a
/// <see cref="Agentweaver.Domain.RunId"/>, so anything that needs the owning run must strip the
/// suffix first.
///
/// <para>
/// Extracted from <see cref="RunStoreSubmittingUserResolver"/> so every consumer normalizes
/// identically. <c>KubernetesSandboxExecutor.PersistAgentHostClaimNameAsync</c> previously did not:
/// it threw "the run id does not parse as a RunId" for every coordinator decompose sub-run, which
/// failed the AgentHost launch and silently degraded decomposition to the deterministic (non-AI)
/// fallback on every orchestration.
/// </para>
/// </summary>
public static class CoordinatorSubRunIds
{
    /// <summary>The synthetic suffixes the coordinator appends to a parent run id.</summary>
    public static readonly IReadOnlyList<string> Suffixes =
    [
        "-coordinator-draft",
        "-coordinator-decompose",
        "-coordinator-orchestrate",
    ];

    /// <summary>
    /// Returns <paramref name="runId"/> with any single coordinator sub-run suffix removed, or the
    /// value unchanged when it carries none.
    /// </summary>
    public static string StripSyntheticSuffix(string runId)
    {
        foreach (var suffix in Suffixes)
        {
            if (runId.EndsWith(suffix, StringComparison.Ordinal))
                return runId[..^suffix.Length];
        }

        return runId;
    }
}
