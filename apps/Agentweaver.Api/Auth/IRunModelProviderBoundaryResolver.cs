using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public sealed record ResolvedRunModelProviderBoundary(
    EffectiveModelProviderResult Provider,
    string? ByokProviderFingerprint);

public interface IRunModelProviderBoundaryResolver
{
    Task<ResolvedRunModelProviderBoundary> ResolveDurableProviderBoundaryAsync(
        Run run,
        CancellationToken ct);
}
