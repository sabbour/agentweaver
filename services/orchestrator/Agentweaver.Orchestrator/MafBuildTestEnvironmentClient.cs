using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed class MafBuildTestEnvironmentClient(
    HttpClient client,
    RuntimeRegistrationOwnerOptions options)
{
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

    private static string OwnerPath(string projectId, string runId, string environmentId) =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
        $"/environments/{Uri.EscapeDataString(environmentId)}/sandbox/build-test";

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
