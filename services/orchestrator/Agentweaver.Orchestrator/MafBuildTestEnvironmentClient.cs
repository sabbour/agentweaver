using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Calls the environment owner for BuildTest preparation, execution, reconciliation, and workspace capture.
/// The MAF dispatcher uses this adapter to preserve owner authorization and translate transport failures into coordination errors.
/// </summary>
internal sealed class MafBuildTestEnvironmentClient(
    HttpClient client,
    RuntimeRegistrationOwnerOptions options)
{
    /// <summary>
    /// Requests an environment-owned binding preparation for a session and execution profile.
    /// </summary>
    /// <param name="actor">The actor authorization forwarded to the environment owner.</param>
    /// <param name="identity">The project, run, and session that will execute the command.</param>
    /// <param name="environmentId">The selected environment.</param>
    /// <param name="profileReference">The accepted execution profile reference.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The owner preparation receipt used to bind the command to the environment.</returns>
    /// <exception cref="CoordinationException">Thrown when authorization or the environment owner request fails.</exception>
    internal async Task<SandboxBuildTestBindingPreparation> PrepareAsync(
        RuntimeActorAuthorization actor,
        SessionIdentity identity,
        string environmentId,
        string profileReference,
        CancellationToken cancellationToken)
    {
        var path = OwnerPath(identity.ProjectId, identity.RunId, environmentId) +
            "/binding-preparation?sessionId=" + Uri.EscapeDataString(identity.SessionId) +
            "&executionProfileReference=" + Uri.EscapeDataString(profileReference);
        return await ExecuteAsync(() =>
            RuntimeOwnerHttpTransport.SendAsync<SandboxBuildTestBindingPreparation>(
                client, options.EnvironmentOwnerAddress, path, actor, null, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a BuildTest command or reconciles an existing running operation with its owner.
    /// </summary>
    /// <param name="actor">The actor authorization forwarded to the environment owner.</param>
    /// <param name="intent">The immutable checkpointed command intent.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The validated owner operation snapshot.</returns>
    /// <remarks>An existing terminal operation is returned without resubmission; a matching running operation is reconciled instead.</remarks>
    internal async Task<SandboxBuildTestOperationSnapshot> ExecuteOrReconcileAsync(
        RuntimeActorAuthorization actor,
        MafExecutionBuildTestIntent intent,
        CancellationToken cancellationToken)
    {
        var path = OwnerPath(intent.Identity.ProjectId, intent.Identity.RunId,
            intent.ExpectedBinding.Fence.Owner.EnvironmentId) + "/commands";
        var operationPath = path + "/" + intent.OperationId.ToString("D");
        return await ExecuteAsync(async () =>
        {
            var existing = await RuntimeOwnerHttpTransport.ReadOptionalAsync<SandboxBuildTestOperationResult>(
                client, options.EnvironmentOwnerAddress, operationPath, actor, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var operation = existing.Operation ?? throw new CoordinationException(
                    "maf_execution_build_test_owner_contract_invalid", StatusCodes.Status502BadGateway);
                if (MafBuildTestCommandContract.Classify(intent, operation) != MafExecutionTaskStatus.Running)
                    return operation;
                path = operationPath + "/reconcile";
            }
            var result = await RuntimeOwnerHttpTransport.SendAsync<SandboxBuildTestOperationResult>(
                client, options.EnvironmentOwnerAddress, path, actor, intent.ToApiRequest(), cancellationToken)
                .ConfigureAwait(false);
            return result.Operation ?? throw new CoordinationException(
                "maf_execution_build_test_owner_contract_invalid", StatusCodes.Status502BadGateway);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the captured workspace bytes for the supplied BuildTest operation.
    /// </summary>
    /// <param name="actor">The actor authorization forwarded to the environment owner.</param>
    /// <param name="intent">The command whose capture is being read.</param>
    /// <param name="reference">The expected capture reference and digest metadata.</param>
    /// <param name="cancellationToken">Cancels the owner read.</param>
    /// <returns>The capture content after bounded owner transport validation.</returns>
    internal Task<byte[]> ReadWorkspaceCaptureAsync(
        RuntimeActorAuthorization actor, MafExecutionBuildTestIntent intent,
        SandboxBuildTestWorkspaceCaptureReference reference, CancellationToken cancellationToken) =>
        ExecuteAsync(() => RuntimeOwnerHttpTransport.ReadBuildTestCaptureAsync(
            client, options.EnvironmentOwnerAddress, OperationPath(intent) + "/workspace-capture",
            actor, reference, cancellationToken));

    /// <summary>
    /// Records the durable Events acknowledgment with the environment owner.
    /// </summary>
    /// <param name="actor">The actor authorization forwarded to the environment owner.</param>
    /// <param name="intent">The command whose capture was acknowledged.</param>
    /// <param name="acknowledgment">The Events journal entry to bind to the operation.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The updated operation snapshot.</returns>
    /// <exception cref="CoordinationException">Thrown unless the owner returns a successful operation bound to this acknowledgment.</exception>
    internal Task<SandboxBuildTestOperationSnapshot> AcknowledgeWorkspaceCaptureAsync(
        RuntimeActorAuthorization actor, MafExecutionBuildTestIntent intent,
        ProducedRunCaptureJournalEntry acknowledgment, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var result = await RuntimeOwnerHttpTransport.SendAsync<SandboxBuildTestOperationResult>(
                client, options.EnvironmentOwnerAddress, OperationPath(intent) + "/workspace-capture/acknowledge",
                actor, new SandboxBuildTestWorkspaceCaptureAcknowledgmentRequest(
                    intent.ToApiRequest(), acknowledgment), cancellationToken).ConfigureAwait(false);
            var operation = result.Operation ??
                throw new CoordinationException("maf_execution_build_test_owner_contract_invalid",
                    StatusCodes.Status502BadGateway);
            if (MafBuildTestCommandContract.Classify(intent, operation) != MafExecutionTaskStatus.Succeeded ||
                operation.WorkspaceCaptureAcknowledgment != acknowledgment)
                throw new CoordinationException("maf_execution_workspace_capture_acknowledgment_conflict",
                    StatusCodes.Status502BadGateway);
            return operation;
        });

    /// <summary>
    /// Builds the owner route for one operation using the intent's immutable identity and environment.
    /// </summary>
    private static string OperationPath(MafExecutionBuildTestIntent intent) =>
        OwnerPath(intent.Identity.ProjectId, intent.Identity.RunId, intent.ExpectedBinding.Fence.Owner.EnvironmentId) +
        "/commands/" + intent.OperationId.ToString("D");

    /// <summary>
    /// Builds the common environment-owner route with escaped path identifiers.
    /// </summary>
    private static string OwnerPath(string projectId, string runId, string environmentId) =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
        $"/environments/{Uri.EscapeDataString(environmentId)}/sandbox/build-test";

    /// <summary>
    /// Maps runtime-owner authorization and HTTP transport failures to orchestration-facing error codes.
    /// </summary>
    /// <typeparam name="T">The owner call's response type.</typeparam>
    /// <param name="action">The asynchronous owner operation.</param>
    /// <returns>The action result.</returns>
    /// <exception cref="CoordinationException">Thrown when the owner denies access or cannot be reached.</exception>
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException failure)
        {
            throw new CoordinationException(
                failure.Code == "runtime_owner_denied"
                    ? "maf_execution_build_test_owner_denied" : "maf_execution_build_test_owner_unavailable",
                failure.Code == "runtime_owner_denied"
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            throw new CoordinationException(
                "maf_execution_build_test_owner_unavailable", StatusCodes.Status502BadGateway);
        }
    }
}
