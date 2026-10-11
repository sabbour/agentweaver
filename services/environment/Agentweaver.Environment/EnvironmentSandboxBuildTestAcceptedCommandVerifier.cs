using System.Globalization;
using System.Collections.Immutable;
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
            var command = await ReadMetadataAsync(checkpoint, actor, cancellationToken).ConfigureAwait(false);
            if (command.Checkpoint != checkpoint)
                throw new RuntimeAuthorizationException("buildtest_command_checkpoint_mismatch");
            if (command.WorkspaceSource is { } source)
            {
                var (ownerPath, query) = CommandAddress(checkpoint);
                var bytes = await RuntimeOwnerHttpTransport.ReadBuildTestSeedAsync(
                    client, options.OrchestratorOwnerAddress, ownerPath + "/workspace-seed" + query,
                    actor, source, cancellationToken).ConfigureAwait(false);
                await VerifyCurrentAsync(command, actor, cancellationToken).ConfigureAwait(false);
                command = command with { WorkspaceSeed = bytes.ToImmutableArray() };
            }
            return command.Validate();
        }
        finally
        {
            actor.Bearer.Invalidate();
        }
    }

    public async Task VerifyCurrentAsync(
        SandboxBuildTestAcceptedCommand acceptedCommand, CancellationToken cancellationToken)
    {
        _ = (acceptedCommand ?? throw new ArgumentNullException(nameof(acceptedCommand))).ValidateMetadata();
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyCurrentAsync(acceptedCommand, actor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            actor.Bearer.Invalidate();
        }
    }

    public async Task VerifyWorkspaceCaptureAcknowledgmentAsync(
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestWorkspaceCaptureAcknowledgmentVerification verification,
        CancellationToken cancellationToken)
    {
        _ = acceptedCommand.ValidateMetadata();
        if (options is null)
            throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");
        verification.Reference.ValidateAcknowledgment(acceptedCommand.WorkspaceSource ??
            throw new RuntimeAuthorizationException("buildtest_capture_source_unavailable"), verification.Capture);
        var actor = await GetActorAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyCurrentAsync(acceptedCommand, actor, cancellationToken).ConfigureAwait(false);
            var (path, _) = CommandAddress(acceptedCommand.Checkpoint);
            var acknowledgment = await RuntimeOwnerHttpTransport.SendAsync<ProducedRunCaptureJournalEntry>(
                client, options.OrchestratorOwnerAddress, path + "/workspace-capture/verify",
                actor, verification, cancellationToken).ConfigureAwait(false);
            if (acknowledgment != verification.Capture)
                throw new RuntimeAuthorizationException("buildtest_capture_acknowledgment_changed");
            await VerifyCurrentAsync(acceptedCommand, actor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            actor.Bearer.Invalidate();
        }
    }

    private async Task VerifyCurrentAsync(
        SandboxBuildTestAcceptedCommand acceptedCommand, RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        var current = await ReadMetadataAsync(acceptedCommand.Checkpoint, actor, cancellationToken)
            .ConfigureAwait(false);
        if (current.Checkpoint != acceptedCommand.Checkpoint || current.ImmutableHash != acceptedCommand.ImmutableHash)
            throw new RuntimeAuthorizationException("buildtest_command_checkpoint_changed");
    }

    private async Task<SandboxBuildTestAcceptedCommand> ReadMetadataAsync(
        SandboxBuildTestCheckpointReference checkpoint, RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        if (options is null)
            throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");
        var (path, query) = CommandAddress(checkpoint);
        var command = (await RuntimeOwnerHttpTransport.SendAsync<SandboxBuildTestAcceptedCommand>(
            client, options.OrchestratorOwnerAddress, path + query, actor, null, cancellationToken)
            .ConfigureAwait(false)).ValidateMetadata();
        return command;
    }

    private static (string Path, string Query) CommandAddress(SandboxBuildTestCheckpointReference checkpoint) =>
        ($"/api/projects/{Uri.EscapeDataString(checkpoint.ProjectId)}" +
            $"/runs/{Uri.EscapeDataString(checkpoint.RunId)}/coordination/sessions/" +
            $"{Uri.EscapeDataString(checkpoint.SessionId)}/build-test/commands/" +
            $"{Uri.EscapeDataString(checkpoint.CheckpointId)}/{Uri.EscapeDataString(checkpoint.StepId)}",
        $"?workPlanId={Uri.EscapeDataString(checkpoint.WorkPlanId)}" +
            $"&checkpointRevision={checkpoint.CheckpointRevision.ToString(CultureInfo.InvariantCulture)}" +
            $"&decisionStateVersion={checkpoint.DecisionStateVersion.ToString(CultureInfo.InvariantCulture)}" +
            $"&executionFence={checkpoint.ExecutionFence.ToString(CultureInfo.InvariantCulture)}" +
            $"&acceptedSelectionHash={Uri.EscapeDataString(checkpoint.AcceptedSelectionHash)}");

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
