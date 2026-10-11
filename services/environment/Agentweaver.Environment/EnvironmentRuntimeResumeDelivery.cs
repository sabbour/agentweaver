using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed partial class EnvironmentRuntimeBootstrapDelivery
{
    private async Task<TResponse> SendToHostAsync<TResponse>(
        EnvironmentRuntimeBootstrapContext placement,
        string path,
        RuntimeActorAuthorization actor,
        object request,
        CancellationToken cancellationToken) where TResponse : class
    {
        var owner = new Uri(placement.ConfigureEndpoint.GetLeftPart(UriPartial.Authority) + "/");
        if (placement.RuntimeEndpoint is not { } endpoint)
            return await RuntimeOwnerHttpTransport.SendAsync<TResponse>(
                client, owner, path, actor, request, cancellationToken).ConfigureAwait(false);
        endpoint.Validate();
        if (owner != endpoint.Address)
            throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
        return await RuntimeOwnerHttpTransport.SendBoundedAsync<TResponse>(
            client, owner, path, actor, request, SessionMaterialValidation.MaximumBytes,
            false, cancellationToken, endpoint).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_host_unavailable");
    }

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
        if (workspace.Placement.ConfigureEndpoint != new Uri(endpoint.Address, "runtime/v1/configure"))
            throw new RuntimeAuthorizationException("runtime_resume_endpoint_changed");
        await RequireCurrentResumeAsync(request, actor, cancellationToken).ConfigureAwait(false);
        var receipt = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeHostWorkspaceResumeReceipt>(
            client, endpoint.Address, "/runtime/v1/workspace/resume", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken, endpoint).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_content_unavailable");
        receipt.ValidateFor(request);
        await RequireCurrentResumeAsync(request, actor, cancellationToken).ConfigureAwait(false);
        return receipt;
    }
}
