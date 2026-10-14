using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentRegistrationApiRequest(string IdempotencyKey)
{
    public EnvironmentRegistrationApiRequest Validate()
    {
        ValidateIdempotencyKey(IdempotencyKey);
        return this;
    }

    internal static void ValidateIdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Environment idempotency keys must be bounded.", nameof(value));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentReleaseApiRequest(long ExpectedLifecycleGeneration, string IdempotencyKey)
{
    public EnvironmentReleaseApiRequest Validate()
    {
        if (ExpectedLifecycleGeneration < 1)
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedLifecycleGeneration),
                "An explicit positive Environment lifecycle generation is required.");
        EnvironmentRegistrationApiRequest.ValidateIdempotencyKey(IdempotencyKey);
        return this;
    }
}

public sealed class EnvironmentLifecycleManager(
    EnvironmentEgressManager authorization,
    IEnvironmentLifecycleProducer lifecycleProducer)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        EnvironmentRegistrationApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ValidateRouteIdentity(projectId, runId);

        var initialAuthorization = await authorization.GetAuthorizedRunEnvironmentControlContextAsync(
            caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        var owner = new EnvironmentOwnerIdentity(
            initialAuthorization.TenantId,
            projectId,
            runId,
            CreateEnvironmentId(initialAuthorization.TenantId, projectId, runId, request.IdempotencyKey));
        var accepted = await authorization.GetAuthorizedRunSelectionAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        EnsureSameAuthorization(initialAuthorization, accepted.Authorization);

        return await lifecycleProducer.RegisterAsync(
            owner,
            request.IdempotencyKey,
            cancellationToken,
            token => RecheckAuthorizationAndSelectionAsync(
                caller, owner, initialAuthorization, accepted.RunSelection, token)).ConfigureAwait(false);
    }

    public async Task<EnvironmentLifecycleTransitionResult> ReleaseAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        EnvironmentReleaseApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ValidateRouteIdentity(projectId, runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);

        var (owner, initialAuthorization) = await authorization.GetAuthorizedRunEnvironmentControlAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var accepted = await authorization.GetAuthorizedRunSelectionAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        EnsureSameAuthorization(initialAuthorization, accepted.Authorization);

        return await lifecycleProducer.TransitionAsync(
            new EnvironmentGenerationFence(owner, request.ExpectedLifecycleGeneration),
            EnvironmentLifecycleState.Released,
            request.IdempotencyKey,
            cancellationToken,
            token => RecheckAuthorizationAndSelectionAsync(
                caller, owner, initialAuthorization, accepted.RunSelection, token)).ConfigureAwait(false);
    }

    internal static string CreateEnvironmentId(
        string tenantId,
        string projectId,
        string runId,
        string idempotencyKey)
    {
        var canonical = string.Concat(
            tenantId.Length, ":", tenantId,
            projectId.Length, ":", projectId,
            runId.Length, ":", runId,
            idempotencyKey.Length, ":", idempotencyKey);
        return "env-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private async Task RecheckAuthorizationAndSelectionAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse originalAuthorization,
        EffectiveNetworkPolicySelection originalSelection,
        CancellationToken cancellationToken)
    {
        var current = await authorization.GetAuthorizedRunSelectionAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        EnsureSameAuthorization(originalAuthorization, current.Authorization);
        if (!JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(originalSelection, JsonOptions),
                JsonSerializer.SerializeToNode(current.RunSelection, JsonOptions)))
            throw new ProjectsConfigApiException(
                "run_selection_changed",
                "The exact accepted run selection changed while the Environment lifecycle operation was pending.");
    }

    private static void EnsureSameAuthorization(
        ProjectAuthorizationContextResponse expected,
        ProjectAuthorizationContextResponse actual)
    {
        if (!EnvironmentEgressManager.SameAuthorizationContext(expected, actual))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the Environment lifecycle operation.");
    }

    private static void ValidateRouteIdentity(string projectId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (projectId.Length > 256 || runId.Length > 256 ||
            projectId.Any(char.IsControl) || runId.Any(char.IsControl))
            throw new ArgumentException("Project and run identifiers must be bounded and contain no control characters.");
    }
}
