using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Sandbox;

internal sealed record AgentHostDispatchBoundary(
    string DispatchRunId,
    string OwningRunId,
    int LifecycleGeneration,
    string? ProjectId,
    string UserId,
    string? AgentName,
    string ProviderKey,
    bool IsResumableAssistant);

internal interface IAgentHostDispatchBoundaryValidator
{
    Task<AgentHostDispatchBoundary> CaptureAsync(string runId, CancellationToken ct);
}

internal sealed class AgentHostDispatchBoundaryValidator(IServiceScopeFactory scopeFactory)
    : IAgentHostDispatchBoundaryValidator
{
    public async Task<AgentHostDispatchBoundary> CaptureAsync(string runId, CancellationToken ct)
    {
        var owningRunId = CoordinatorSubRunIds.StripSyntheticSuffix(runId);
        if (!RunId.TryParse(owningRunId, out var parsedRunId))
            throw new InvalidOperationException($"AgentHost dispatch run id '{runId}' is invalid.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<IRunStore>()
            .GetAsync(parsedRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"AgentHost dispatch run '{owningRunId}' was not found.");
        if (run.Status != RunStatus.InProgress)
        {
            throw new WorkflowAgentInfrastructureException(
                "agenthost_dispatch_inactive",
                $"AgentHost dispatch run '{owningRunId}' is no longer active.");
        }
        if (string.IsNullOrWhiteSpace(run.SubmittingUser))
            throw new InvalidOperationException($"AgentHost dispatch run '{owningRunId}' has no submitting user.");
        var isResumableAssistant = string.Equals(
            run.AgentName, AssistantRunService.OperatorAgentName, StringComparison.Ordinal);
        if (!isResumableAssistant && run.ProjectId is { } projectId)
        {
            var caller = new CallerContext
            {
                User = run.SubmittingUser,
                EntraObjectId = run.SubmittingUser,
            };
            if (!await scope.ServiceProvider.GetRequiredService<IProjectRoleAuthorizationService>()
                    .HasRoleAsync(caller, projectId, ProjectRole.Contributor, ct)
                    .ConfigureAwait(false))
            {
                throw new AgentProviderException(
                    run.ModelSource,
                    AgentProviderFailureKind.Authorization,
                    "project_authorization_required",
                    "The submitting user is no longer authorized to run this project agent.",
                    isRetryable: false);
            }
        }

        var provider = await scope.ServiceProvider.GetRequiredService<RunModelInvocationGuard>()
            .PrepareAsync(runId, ct).ConfigureAwait(false);
        var providerKey = provider.Provider.ProviderKey();
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new InvalidOperationException($"AgentHost dispatch run '{owningRunId}' has no provider snapshot key.");

        return new AgentHostDispatchBoundary(
            runId,
            owningRunId,
            run.LifecycleGeneration,
            run.ProjectId?.ToString(),
            run.SubmittingUser,
            run.AgentName,
            providerKey,
            isResumableAssistant);
    }
}
