using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Input for creating the active Environment owned by one project run.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnvironmentRegistrationApiRequest(
    string IdempotencyKey,
    P1InvocationBindingExpectation? P1Binding = null)
{
    /// <summary>Validates the registration key and returns this request for fluent endpoint handling.</summary>
    public EnvironmentRegistrationApiRequest Validate()
    {
        ValidateIdempotencyKey(IdempotencyKey);
        P1Binding?.Validate();
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
    IEnvironmentLifecycleProducer lifecycleProducer,
    IEnvironmentP1InvocationBindingReader? p1BindingReader = null)
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
        CancellationToken cancellationToken,
        RuntimeActorAuthorization? actor = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ValidateRouteIdentity(projectId, runId);

        ProjectAuthorizationContextResponse initialAuthorization;
        EnvironmentOwnerIdentity owner;
        if (request.P1Binding is not null)
        {
            initialAuthorization = await authorization.GetAuthorizedRunEnvironmentPlacementReadContextAsync(
                caller, projectId, runId, cancellationToken).ConfigureAwait(false);
            owner = new EnvironmentOwnerIdentity(
                initialAuthorization.TenantId,
                projectId,
                runId,
                CreateEnvironmentId(
                    initialAuthorization.TenantId, projectId, runId, request.IdempotencyKey));
        }
        else
        {
            initialAuthorization = await authorization.GetAuthorizedRunEnvironmentControlContextAsync(
                caller, projectId, runId, cancellationToken).ConfigureAwait(false);
            owner = new EnvironmentOwnerIdentity(
                initialAuthorization.TenantId,
                projectId,
                runId,
                CreateEnvironmentId(initialAuthorization.TenantId, projectId, runId, request.IdempotencyKey));
        }
        var accepted = request.P1Binding is null
            ? await authorization.GetAuthorizedRunSelectionAsync(
                caller, owner, cancellationToken).ConfigureAwait(false)
            : await authorization.GetAuthorizedRunEnvironmentSelectionAsync(
                caller, owner, cancellationToken).ConfigureAwait(false);
        EnsureSameAuthorization(initialAuthorization, accepted.Authorization);

        P1InvocationBinding? p1Binding = null;
        if (request.P1Binding is { } expectation)
            p1Binding = await ReadCurrentP1BindingAsync(
                actor, owner, accepted.Authorization, expectation, cancellationToken).ConfigureAwait(false);

        async Task BeforeCommitAsync(CancellationToken token)
        {
            await RecheckAuthorizationAndSelectionAsync(
                caller,
                owner,
                initialAuthorization,
                accepted.RunSelection,
                request.P1Binding is not null,
                token).ConfigureAwait(false);
            if (p1Binding is not null)
            {
                var current = await ReadCurrentP1BindingAsync(
                    actor,
                    owner,
                    accepted.Authorization,
                    request.P1Binding!,
                    token).ConfigureAwait(false);
                if (current != p1Binding)
                    throw new RuntimeAuthorizationException("p1_invocation_binding_changed");
            }
        }

        return p1Binding is null
            ? await lifecycleProducer.RegisterAsync(
                owner, request.IdempotencyKey, cancellationToken, BeforeCommitAsync).ConfigureAwait(false)
            : await lifecycleProducer.RegisterP1Async(
                owner, request.IdempotencyKey, p1Binding, cancellationToken, BeforeCommitAsync)
                .ConfigureAwait(false);
    }

    private Task<P1InvocationBinding> ReadCurrentP1BindingAsync(
        RuntimeActorAuthorization? actor,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse authorizationContext,
        P1InvocationBindingExpectation expectation,
        CancellationToken cancellationToken) =>
        EnvironmentP1InvocationBindingVerifier.ReadAndValidateAsync(
            p1BindingReader ??
                throw new RuntimeAuthorizationException("p1_invocation_binding_unavailable"),
            actor,
            owner,
            authorizationContext,
            expectation,
            cancellationToken);

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
                caller, owner, initialAuthorization, accepted.RunSelection,
                requireRunBoundReadSelection: false, cancellationToken: token)).ConfigureAwait(false);
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
        bool requireRunBoundReadSelection,
        CancellationToken cancellationToken)
    {
        var current = requireRunBoundReadSelection
            ? await authorization.GetAuthorizedRunEnvironmentSelectionAsync(
                caller, owner, cancellationToken).ConfigureAwait(false)
            : await authorization.GetAuthorizedRunSelectionAsync(
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
