namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Thrown by the AgentHost pre-launch capacity gate (<see
/// cref="IAgentHostPodLifecycle.CheckAgentHostCapacityAsync"/> /
/// <see cref="KubernetesSandboxExecutor.LaunchAgentHostPodAsync"/>) when the namespace ResourceQuota
/// does not currently have enough CPU headroom to admit another AgentHost pod (each pod's
/// <c>limits.cpu</c> is 2 cores).
///
/// <para>
/// Unlike <see cref="AgentHostQuotaExceededException"/>, this is <b>not</b> a hard failure — it is a
/// <i>retry signal</i>. The dispatch engine catches it, parks the subtask in
/// <c>SubtaskStatus.PendingCapacity</c>, and retries after a back-off. Capacity may free up at any
/// time once the <c>AgentHostReaperService</c> reaps orphaned pods or the node pool scales out, so
/// the run should queue rather than fail.
/// </para>
/// </summary>
public sealed class AgentHostCapacityPendingException : Exception
{
    /// <summary>CPU cores currently used against the namespace quota at the time of the check.</summary>
    public double UsedCpu { get; }

    /// <summary>Hard CPU-core limit configured on the namespace quota.</summary>
    public double HardCpu { get; }

    /// <summary>
    /// Why capacity is pending. <c>"quota_exceeded"</c> when the namespace ResourceQuota is full;
    /// the generic <c>"scaling"</c> covers the case where capacity could become available once the
    /// node pool scales out. Recorded against the parked subtask for diagnostics.
    /// </summary>
    public string Reason { get; }

    public AgentHostCapacityPendingException(double used, double hard, string reason = "quota_exceeded")
        : base($"Agent pod capacity pending ({reason}): {used}/{hard} CPU used. " +
               "Queued for retry until capacity frees up.")
    {
        UsedCpu = used;
        HardCpu = hard;
        Reason = string.IsNullOrWhiteSpace(reason) ? "scaling" : reason;
    }
}
