using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public sealed record ResolvedRunModelProviderBoundary(
    EffectiveModelProviderResult Provider,
    string? ByokProviderFingerprint,
    ByokProviderConfiguration? ByokProviderConfiguration = null)
{
    public string? ResolveEffectiveModelId(string? requestedModelId) =>
        Provider is EffectiveModelProviderResult.Byok
            ? ByokProviderConfiguration?.Model
                ?? throw new InvalidOperationException(
                    "The accepted BYOK provider boundary is missing its frozen model configuration.")
            : requestedModelId;
}

public interface IRunModelProviderBoundaryResolver
{
    Task<ResolvedRunModelProviderBoundary> ResolveDurableProviderBoundaryAsync(
        Run run,
        CancellationToken ct);
}
