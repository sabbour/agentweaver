namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Thrown by <see cref="KubernetesSandboxExecutor.LaunchAgentHostPodAsync"/> when the namespace
/// ResourceQuota does not have enough CPU headroom to admit another AgentHost pod (each pod's
/// <c>limits.cpu</c> is 2 cores). Surfaced so the launch path can fail the run with a precise
/// reason (<c>agent_quota_exceeded</c>) instead of the generic "run interrupted" message that the
/// controller emits when the pod is rejected with <c>exceeded quota</c>.
/// </summary>
public sealed class AgentHostQuotaExceededException : Exception
{
    /// <summary>CPU cores currently used against the namespace quota at the time of the check.</summary>
    public double UsedCpu { get; }

    /// <summary>Hard CPU-core limit configured on the namespace quota.</summary>
    public double HardCpu { get; }

    public AgentHostQuotaExceededException(double used, double hard)
        : base($"Agent pod quota exceeded: {used}/{hard} CPU used. Try again when capacity is available.")
    {
        UsedCpu = used;
        HardCpu = hard;
    }
}
