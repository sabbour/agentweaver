namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Manages the lifecycle of per-run <c>Agentweaver.AgentHost</c> pods — the sandbox
/// pods that host the A2A-exposed <c>CopilotAIAgent</c> leaf when
/// <c>Sandbox:AgentExecutionMode=pod-per-run</c>.
///
/// <para>
/// When the workflow graph suspends (HITL <c>RequestPort</c> gate, coordinator-idle
/// awaiting children), the pod is checkpoint-released via
/// <see cref="ReleaseAgentHostPodAsync"/>; on resume it is re-claimed and rehydrated
/// from the durable DB-backed <c>ICheckpointStore</c> (Q3 hybrid, spec §9/§12.2).
/// </para>
///
/// <para>
/// Seam for Tank: the returned endpoint URL is persisted in
/// <see cref="IPodNameRegistry"/> so the worker-side <c>RemoteAgentProxy</c> can
/// build the A2A client pointing at <c>{endpointUrl}/v1/message:stream</c>.
/// </para>
/// </summary>
public interface IAgentHostPodLifecycle
{
    /// <summary>
    /// Provisions (or re-provisions after a suspend-release) an AgentHost pod for the
    /// given run. Waits until the pod is <c>Bound</c> and returns the base A2A endpoint
    /// URL (<c>http[s]://&lt;podIP&gt;:&lt;port&gt;&lt;a2aPath&gt;</c>).
    ///
    /// <para>Registers the endpoint in <see cref="IPodNameRegistry"/> on success.</para>
    /// </summary>
    /// <returns>
    /// The fully-qualified A2A base URL, e.g.
    /// <c>http://10.0.1.5:8080/a2a/agent</c>.
    /// </returns>
    Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Provisions an AgentHost pod for <paramref name="runId"/> and configures it with an explicit
    /// shared working directory instead of the run row's default worktree.
    /// </summary>
    Task<string> LaunchAgentHostPodAsync(
        string runId,
        string? workingDirectoryOverride,
        CancellationToken ct = default);

    /// <summary>
    /// Provisions an AgentHost with an explicit purpose and immutable source revision. The default
    /// implementation preserves compatibility for lifecycle fakes/providers that only understand a
    /// shared working-directory override. Pod-local paths are created inside AgentHost and must never
    /// be treated as API-visible worktrees by this fallback. Compatibility providers do not report an
    /// effective pod-local path, so preview continues from <see cref="AgentHostLaunchContext.SharedWorkingDirectory"/>.
    /// </summary>
    Task<string> LaunchAgentHostPodAsync(
        string runId,
        AgentHostLaunchContext context,
        CancellationToken ct = default) =>
        LaunchAgentHostPodAsync(runId, context.SharedWorkingDirectory, ct);

    /// <summary>
    /// Releases the AgentHost pod for the given run by deleting its
    /// <c>SandboxClaim</c>. Called on workflow suspension (HITL / coordinator-idle)
    /// when <c>Sandbox:ReleasePodOnSuspend=true</c>.
    /// </summary>
    Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// FENCED release: deletes the run's <c>SandboxClaim</c> only while it is still the one stamped
    /// with <paramref name="holderToken"/> (<see cref="AgentHostLaunchContext.HolderToken"/>).
    /// Returns <see langword="true"/> when the claim was released (or was already gone), and
    /// <see langword="false"/> when a DIFFERENT holder now owns it, in which case nothing is deleted.
    ///
    /// <para>
    /// A claim is addressed by a deterministic name derived from the run id, so an owner that has
    /// since lost the conversation — e.g. an API replica whose process-local pod-hold state went
    /// stale after the next turn landed on the other replica — would otherwise delete a claim that
    /// another replica is actively serving a turn from. This is the compare-and-swap that prevents
    /// it. The unfenced <see cref="ReleaseAgentHostPodAsync"/> remains correct for callers that are
    /// deliberately reclaiming whatever is there (the cross-replica reaper, turn-scoped failure
    /// paths that just bound the claim themselves).
    /// </para>
    ///
    /// <para>
    /// The default implementation falls back to the unfenced release, preserving behaviour for
    /// lifecycle doubles and non-Kubernetes providers that have no claim to stamp.
    /// </para>
    /// </summary>
    async Task<bool> TryReleaseHeldAgentHostPodAsync(
        string runId,
        string holderToken,
        CancellationToken ct = default)
    {
        await ReleaseAgentHostPodAsync(runId, ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Run-scoped inputs delivered to the warm AgentHost through <c>POST /configure</c>.</summary>
/// <param name="HolderToken">
/// Optional fencing token stamped on the run's <c>SandboxClaim</c> when this launch creates it, so a
/// later <see cref="IAgentHostPodLifecycle.TryReleaseHeldAgentHostPodAsync"/> can prove the claim it
/// is about to delete is still the one its caller created, rather than a newer one another API
/// replica has since put in its place under the same deterministic name.
/// </param>
public sealed record AgentHostLaunchContext(
    string? SharedWorkingDirectory,
    string? SourceRepositoryPath = null,
    string? SourceRef = null,
    string? BaseCommitSha = null,
    string? ExpectedTreeHash = null,
    Agentweaver.Domain.ExecutionWorkspaceMode WorkspaceMode = Agentweaver.Domain.ExecutionWorkspaceMode.Shared,
    Agentweaver.Domain.AgentHostPurpose Purpose = Agentweaver.Domain.AgentHostPurpose.Default,
    string? ScratchRoot = null,
    string? CommitAuthorName = null,
    string? CommitAuthorEmail = null,
    string? CallerBearerToken = null,
    string? HolderToken = null)
{
    /// <summary>
    /// Whether this launch must resolve its effective model provider at PLATFORM scope
    /// (<c>projectId: null</c>) rather than at the launching run's own project scope.
    ///
    /// <para>
    /// True only for <see cref="Agentweaver.Domain.AgentHostPurpose.OperatorAssistant"/> — the
    /// personal operator "Session" conversations. Those runs are not project-scoped work: a
    /// session's <c>Run.ProjectId</c> merely records the project the human happened to be viewing
    /// when they opened the chat, and it is deliberately kept only as incidental MCP/UI context.
    /// <c>AssistantRunService</c> therefore selects the session's provider, and
    /// <c>RunGitHubCapabilitySnapshotLifecycle</c> validates its credential, at platform scope; the
    /// pod that actually serves the conversation must be configured from the very same scope or the
    /// three disagree (a session labelled and gated as platform BYOK could be configured for an
    /// incidental project's Copilot binding, or the reverse).
    /// </para>
    ///
    /// <para>
    /// Every other purpose — coordinator runs, subtasks, retries, Build/Test — is genuine
    /// project-scoped work and keeps resolving against its real project id.
    /// </para>
    /// </summary>
    public bool ResolvesModelProviderAtPlatformScope =>
        Purpose == Agentweaver.Domain.AgentHostPurpose.OperatorAssistant;
}
