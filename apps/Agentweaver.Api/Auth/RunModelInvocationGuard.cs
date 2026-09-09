using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>Revalidates the existing run boundary; provenance is evidence, never provider selection.</summary>
public sealed class RunModelInvocationGuard(IServiceScopeFactory scopeFactory) : IModelInvocationGuard
{
    public async Task<ResolvedRunModelProviderBoundary> PrepareAsync(
        string runId, CancellationToken ct, bool supportsByok = true)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var owningRunId = CoordinatorSubRunIds.StripSyntheticSuffix(runId);
        var run = await services.GetRequiredService<IRunStore>()
            .GetAsync(RunId.Parse(owningRunId), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The model invocation has no persisted run.");
        var boundary = await services.GetRequiredService<IRunModelProviderBoundaryResolver>()
            .ResolveDurableProviderBoundaryAsync(run, ct).ConfigureAwait(false);
        if (boundary.Provider is EffectiveModelProviderResult.Unavailable
            || (!supportsByok && boundary.Provider is EffectiveModelProviderResult.Byok))
        {
            throw new AgentProviderException(
                boundary.Provider.ToModelSource(), AgentProviderFailureKind.Configuration,
                "model_provider_changed", "This model operation requires GitHub Copilot.", isRetryable: true);
        }

        if (boundary.Provider is not EffectiveModelProviderResult.Byok)
        {
            var prepared = await services.GetRequiredService<RunGitHubCapabilitySnapshotLifecycle>()
                .PrepareForUnattendedCopilotLaunchAsync(
                    run, ct, platformScoped: run.AgentName == "Operator",
                    expectedCopilotBindingId: boundary.Provider.ProviderId(),
                    expectedCopilotCredentialVersion: boundary.Provider.CredentialVersion()).ConfigureAwait(false);
            if (!prepared)
                throw new AgentProviderException(
                    boundary.Provider.ToModelSource(), AgentProviderFailureKind.Configuration,
                    "model_provider_changed", "The accepted model credential changed before invocation.", isRetryable: true);
        }

        await services.GetRequiredService<IRunEventStream>().AppendAsync(
            owningRunId,
            new RunEvent(0, EventTypes.RunModelProviderResolved,
                boundary.Provider.ToProvenancePayload(
                    owningRunId, run.ModelId,
                    run.AgentName == "Operator"
                        ? EffectiveModelProviderProvenance.ScopePlatform
                        : EffectiveModelProviderProvenance.ScopeProject),
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
        return boundary;
    }

    public async Task ValidateAsync(string runId, CancellationToken ct) =>
        _ = await PrepareAsync(runId, ct).ConfigureAwait(false);
}
