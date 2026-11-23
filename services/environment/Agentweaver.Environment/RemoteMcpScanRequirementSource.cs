using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;

namespace Agentweaver.Environment;

public interface IRemoteMcpScanRequirementSource
{
    Task<ImmutableArray<RemoteMcpScanRequirement>> ReadCurrentAsync(
        CancellationToken cancellationToken);
}

internal sealed class RemoteMcpScanRequirementSource(
    PlatformPolicySourceHttpClient? policySource) : IRemoteMcpScanRequirementSource
{
    public async Task<ImmutableArray<RemoteMcpScanRequirement>> ReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        if (policySource is null)
            throw new RemoteMcpConnectionException(
                "remote_mcp_policy_requirements_unavailable",
                "The current Tool Permissions policy is unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        var result = await policySource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Observation is null)
            throw new RemoteMcpConnectionException(
                "remote_mcp_policy_requirements_unavailable",
                "The current Tool Permissions policy is unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        try
        {
            return NativeAcsUnifiedPolicyProfile.ReadRemoteMcpScanRequirements(
                result.Observation.Artifact);
        }
        catch (ArgumentException)
        {
            throw new RemoteMcpConnectionException(
                "remote_mcp_policy_requirements_invalid",
                "The current Tool Permissions policy does not define valid Remote MCP requirements.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }
}
