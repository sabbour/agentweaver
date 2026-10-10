using System.Globalization;
using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Agentweaver.Environment;

public sealed class EnvironmentSandboxBuildTestAcceptedCommandVerifier(
    HttpClient client,
    IHttpContextAccessor contextAccessor,
    TimeProvider timeProvider,
    EnvironmentRuntimeBootstrapDeliveryOptions? options = null) : ISandboxBuildTestAcceptedCommandVerifier
{
    public async Task<SandboxBuildTestAcceptedCommand> ResolveAsync(
        SandboxBuildTestCheckpointReference checkpoint,
        CancellationToken cancellationToken)
    {
        checkpoint = (checkpoint ?? throw new ArgumentNullException(nameof(checkpoint))).Validate();
        if (options is null)
            throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");

        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = $"/api/projects/{Uri.EscapeDataString(checkpoint.ProjectId)}" +
                $"/runs/{Uri.EscapeDataString(checkpoint.RunId)}/coordination/sessions/" +
                $"{Uri.EscapeDataString(checkpoint.SessionId)}/build-test/commands/" +
                $"{Uri.EscapeDataString(checkpoint.CheckpointId)}/{Uri.EscapeDataString(checkpoint.StepId)}" +
                $"?workPlanId={Uri.EscapeDataString(checkpoint.WorkPlanId)}" +
                $"&checkpointRevision={checkpoint.CheckpointRevision.ToString(CultureInfo.InvariantCulture)}" +
                $"&decisionStateVersion={checkpoint.DecisionStateVersion.ToString(CultureInfo.InvariantCulture)}" +
                $"&executionFence={checkpoint.ExecutionFence.ToString(CultureInfo.InvariantCulture)}" +
                $"&acceptedSelectionHash={Uri.EscapeDataString(checkpoint.AcceptedSelectionHash)}";
            var command = await RuntimeOwnerHttpTransport.SendAsync<SandboxBuildTestAcceptedCommand>(
                client,
                options.OrchestratorOwnerAddress,
                path,
                actor,
                null,
                cancellationToken).ConfigureAwait(false);
            command = command.Validate();
            if (command.Checkpoint != checkpoint)
                throw new RuntimeAuthorizationException("buildtest_command_checkpoint_mismatch");
            return command;
        }
        finally
        {
            actor.Bearer.Invalidate();
        }
    }

    private async Task<RuntimeActorAuthorization> GetActorAsync(CancellationToken cancellationToken)
    {
        var context = contextAccessor.HttpContext
            ?? throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        if (!authentication.Succeeded ||
            authentication.Properties?.ExpiresUtc is not { } expiresAt ||
            expiresAt <= timeProvider.GetUtcNow() ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
            throw new RuntimeAuthorizationException("runtime_owner_denied");

        cancellationToken.ThrowIfCancellationRequested();
        var bearer = new SecretCredential(header.Parameter, expiresAt, timeProvider);
        return new(bearer, context.Request.Headers["X-Agentweaver-Tenant"].SingleOrDefault());
    }
}
