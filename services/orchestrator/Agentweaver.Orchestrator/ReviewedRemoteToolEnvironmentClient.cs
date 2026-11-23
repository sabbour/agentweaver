using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

internal sealed class ReviewedRemoteToolEnvironmentClient(
    HttpClient client,
    [FromKeyedServices(RemoteMcpCallVerificationComposition.TlsKey)]
    RuntimeServiceTlsOptions tlsOptions) : IReviewedRemoteToolEnvironmentReader
{
    private readonly Uri _ownerAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(
        tlsOptions.Validate("Orchestrator", "Environment").PeerOwnerAddress!, UriKind.Absolute));

    public async Task<ReviewedRemoteToolEnvironmentState> ReadAsync(
        HttpContext context,
        RuntimeRegistration registration,
        ReviewedRemoteToolSnapshot snapshot,
        ReviewedRemoteToolCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(call);
        var binding = registration.Binding;
        if (string.IsNullOrWhiteSpace(binding.EnvironmentId) ||
            binding.EnvironmentLifecycleGeneration <= 0)
            throw new RuntimeAuthorizationException("runtime_remote_tool_gateway_invalid");
        var request = new RemoteMcpGatewayEnvironmentStateRequest(
            new(
                new(binding.TenantId, binding.ProjectId, binding.RunId, binding.EnvironmentId),
                binding.EnvironmentLifecycleGeneration),
            ReviewedRemoteToolAgent.SnapshotBinding(snapshot));
        try
        {
            using var response = await client.PostAsJsonAsync(
                new Uri(_ownerAddress, EnvironmentStatePath),
                request,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var state = await response.Content.ReadFromJsonAsync<RemoteMcpGatewayEnvironmentState>(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (state is null)
                throw new RuntimeAuthorizationException("runtime_remote_tool_gateway_invalid");
            return new(state.GatewayDescriptorSha256, state.EnforcementGeneration);
        }
        catch (HttpRequestException exception)
        {
            throw new RuntimeAuthorizationException(
                "runtime_remote_tool_gateway_unavailable", null, exception);
        }
    }

    private const string EnvironmentStatePath = "/internal/runtime/remote-mcp/environment-state";
}
