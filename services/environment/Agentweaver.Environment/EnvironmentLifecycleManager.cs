using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Input for creating the active Environment owned by one project run.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentRegistrationApiRequest(string IdempotencyKey)
{
    /// <summary>Validates the registration key and returns this request for fluent endpoint handling.</summary>
    public EnvironmentRegistrationApiRequest Validate()
    {
        ValidateIdempotencyKey(IdempotencyKey);
        return this;
    }

    /// <summary>Rejects missing, oversized, or control-character-containing operation keys.</summary>
    internal static void ValidateIdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Environment idempotency keys must be bounded.", nameof(value));
    }
}

/// <summary>Input for releasing an Environment at an explicitly observed lifecycle generation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentReleaseApiRequest(long ExpectedLifecycleGeneration, string IdempotencyKey)
{
    /// <summary>Requires a positive generation fence and a valid idempotency key.</summary>
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

/// <summary>
/// Coordinates project authorization with Environment lifecycle registration and release operations.
/// </summary>
public sealed class EnvironmentLifecycleManager(
    EnvironmentEgressManager authorization,
    IEnvironmentLifecycleProducer lifecycleProducer)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the run's deterministic Environment identity and registers it as active.
    /// Authorization and the exact accepted run selection are checked again before the lifecycle commit.
    /// </summary>
    /// <exception cref="ProjectsConfigApiException">The caller is not authorized or the accepted selection changed.</exception>
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

    /// <summary>
    /// Releases the selected Environment only if its lifecycle generation and current run authority still match.
    /// </summary>
    /// <exception cref="ProjectsConfigApiException">Authorization or the accepted run selection changed.</exception>
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

    /// <summary>Derives a stable Environment ID from tenant, project, run, and idempotency-key material.</summary>
    /// <remarks>Length prefixes make the concatenated components unambiguous before the SHA-256 hash is computed.</remarks>
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

    /// <summary>Re-reads caller authority and the run selection immediately before a lifecycle transition commits.</summary>
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

    /// <summary>Rejects a lifecycle operation when the current authorization context differs from the accepted one.</summary>
    private static void EnsureSameAuthorization(
        ProjectAuthorizationContextResponse expected,
        ProjectAuthorizationContextResponse actual)
    {
        if (!EnvironmentEgressManager.SameAuthorizationContext(expected, actual))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the Environment lifecycle operation.");
    }

    /// <summary>Requires bounded, non-empty route identifiers without control characters.</summary>
    private static void ValidateRouteIdentity(string projectId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (projectId.Length > 256 || runId.Length > 256 ||
            projectId.Any(char.IsControl) || runId.Any(char.IsControl))
            throw new ArgumentException("Project and run identifiers must be bounded and contain no control characters.");
    }
}
