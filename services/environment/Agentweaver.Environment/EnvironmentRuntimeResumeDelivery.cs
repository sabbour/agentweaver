using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Implements authenticated requests to a placed runtime host for resume-related operations.</summary>
public sealed partial class EnvironmentRuntimeBootstrapDelivery
{
    /// <summary>Sends a host request only to the validated receiver endpoint for the current placement.</summary>
    private async Task<TResponse> SendToHostAsync<TResponse>(
        EnvironmentRuntimeBootstrapContext placement,
        string path,
        RuntimeActorAuthorization actor,
        object request,
        CancellationToken cancellationToken) where TResponse : class
    {
        if (placement.RuntimeEndpoint is not { } endpoint)
        {
            var legacyOwner = new Uri(placement.ConfigureEndpoint.GetLeftPart(UriPartial.Authority) + "/");
            return await RuntimeOwnerHttpTransport.SendAsync<TResponse>(
                client, legacyOwner, path, actor, request, cancellationToken).ConfigureAwait(false);
        }
        var configureEndpoint = endpoint.ReceiverConfigureEndpoint
            ?? throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
        try
        {
            endpoint.ValidateForConfigureEndpoint(configureEndpoint);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
        }
        if (placement.ConfigureEndpoint != configureEndpoint)
            throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
        var receiverOwner = new Uri(configureEndpoint.GetLeftPart(UriPartial.Authority) + "/");
        return await RuntimeOwnerHttpTransport.SendBoundedAsync<TResponse>(
            client, receiverOwner, path, actor, request, SessionMaterialValidation.MaximumBytes,
            false, cancellationToken, endpoint).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_host_unavailable");
    }

    /// <summary>Confirms that the orchestrator still accepts the exact workspace resume request.</summary>
    public async Task RequireCurrentResumeAsync(
        RuntimeWorkspaceResumeRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        var echo = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeWorkspaceResumeRequest>(
            client, options.OrchestratorOwnerAddress, "/internal/runtime/resume/require-current",
            actor, request, SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false);
        if (echo is null || !actor.Bearer.IsUsable() || !SameSuspendJson(request, echo))
            throw new RuntimeAuthorizationException("runtime_resume_authority_stale");
    }

    /// <summary>Asks the current host to verify restored workspace content and rechecks request authority afterward.</summary>
    public async Task<RuntimeHostWorkspaceResumeReceipt> VerifyResumeWorkspaceAsync(
        RuntimeWorkspaceResumeRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var workspace = request.RestoredWorkspace
            ?? throw new RuntimeAuthorizationException("runtime_resume_workspace_required");
        var endpoint = workspace.Placement.RuntimeEndpoint?.Validate()
            ?? throw new RuntimeAuthorizationException("runtime_resume_endpoint_unavailable");
        var configureEndpoint = endpoint.ReceiverConfigureEndpoint
            ?? throw new RuntimeAuthorizationException("runtime_resume_endpoint_changed");
        try
        {
            _ = endpoint.ValidateForConfigureEndpoint(configureEndpoint);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_resume_endpoint_changed");
        }
        if (workspace.Placement.ConfigureEndpoint != configureEndpoint)
            throw new RuntimeAuthorizationException("runtime_resume_endpoint_changed");
        await RequireCurrentResumeAsync(request, actor, cancellationToken).ConfigureAwait(false);
        var receiverOwner = new Uri(configureEndpoint.GetLeftPart(UriPartial.Authority) + "/");
        var receipt = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeHostWorkspaceResumeReceipt>(
            client, receiverOwner, "/runtime/v1/workspace/resume", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken, endpoint).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_content_unavailable");
        receipt.ValidateFor(request);
        await RequireCurrentResumeAsync(request, actor, cancellationToken).ConfigureAwait(false);
        return receipt;
    }
}
