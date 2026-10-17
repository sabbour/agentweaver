using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>
/// Trusted in-memory context created only after authentication, current registration checks,
/// and owner verification of a remote tool-result proof. Its string representation is redacted.
/// </summary>
// The trusted host creates this after authentication and fresh Orchestrator verification.
// It is never a request DTO. The native handler must repeat current-owner checks before IO.
public sealed class RemoteMcpNativeCallContext
{
    private RemoteMcpNativeCallContext(
        RemoteToolResultProof proof, RuntimeRegistration registration,
        RuntimeActorAuthorization actor, DateTimeOffset expiresAt, TimeProvider timeProvider)
    {
        Proof = proof;
        Registration = registration;
        Actor = actor;
        ExpiresAt = expiresAt;
        _timeProvider = timeProvider;
    }

    private readonly TimeProvider _timeProvider;
    [JsonIgnore] public RemoteToolResultProof Proof { get; }
    [JsonIgnore] public RuntimeRegistration Registration { get; }
    [JsonIgnore] public RuntimeActorAuthorization Actor { get; }
    [JsonIgnore] public DateTimeOffset ExpiresAt { get; }
    public override string ToString() => nameof(RemoteMcpNativeCallContext) + " [REDACTED]";

    /// <summary>
    /// Validates the authenticated actor, proof, registration, expiry, and owner verification
    /// receipt before constructing a native call context.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Any actor, expiry, or proof binding is invalid.</exception>
    public static RemoteMcpNativeCallContext CreateVerified(
        ClaimsPrincipal authenticatedPrincipal,
        string validatedIssuer,
        RemoteToolResultProof proof,
        RuntimeRegistration currentRegistration,
        RemoteToolResultVerificationReceipt currentVerification,
        RuntimeActorAuthorization actor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(authenticatedPrincipal);
        ArgumentNullException.ThrowIfNull(currentRegistration);
        ArgumentNullException.ThrowIfNull(currentVerification);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        var subject = authenticatedPrincipal.FindAll("sub").Take(2).ToArray();
        var expiry = authenticatedPrincipal.FindAll("exp").Take(2).ToArray();
        if (authenticatedPrincipal.Identity?.IsAuthenticated != true || subject.Length != 1 ||
            subject[0].Value != proof.ActorId || !Guid.TryParseExact(subject[0].Value, "D", out _) ||
            !Uri.TryCreate(validatedIssuer, UriKind.Absolute, out var issuer) ||
            proof.ActorIssuer != issuer.AbsoluteUri ||
            expiry.Length != 1 ||
            !long.TryParse(expiry[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 253402300799 ||
            actor.TenantSelector != proof.TenantId || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("remote_mcp_native_actor_invalid");
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (expiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("remote_mcp_native_actor_expired");
        var binding = currentRegistration.Binding;
        if (currentRegistration.State != RuntimeRegistrationState.Active ||
            currentRegistration.ExpiresAt <= timeProvider.GetUtcNow() ||
            currentRegistration.RuntimeInstanceId != proof.RuntimeInstanceId ||
            currentRegistration.Revision != proof.RegistrationRevision ||
            binding.ActorIssuer != proof.ActorIssuer || binding.ActorId != proof.ActorId ||
            binding.TenantId != proof.TenantId || binding.ProjectId != proof.ProjectId ||
            binding.RunId != proof.RunId || binding.SessionId != proof.SessionId ||
            binding.WorkflowStepId != proof.StepId || binding.ProjectRevision != proof.ProjectRevision ||
            binding.ProjectConfigurationRevision != proof.ProjectConfigurationRevision ||
            binding.PlatformRuntimeRevision != proof.PlatformRuntimeRevision ||
            binding.ContextRevision != proof.ContextRevision ||
            binding.AcceptedSelectionHash != proof.AcceptedSelectionHash ||
            binding.ExecutionFence != proof.ExecutionFence ||
            binding.AgentId != proof.Snapshot.AgentId ||
            currentVerification.ContractVersion != 1 ||
            currentVerification.ProofSha256 != SessionMaterialValidation.RemoteToolResultProofHash(proof) ||
            currentVerification.ResultEventId != SessionMaterialValidation.RemoteToolResultEventId(proof))
            throw new RuntimeAuthorizationException("remote_mcp_native_call_binding_invalid");
        return new(proof, currentRegistration, actor, expiresAt, timeProvider);
    }

    /// <summary>Rejects use after the bearer, actor token, or registration has expired.</summary>
    public void RequireUsable()
    {
        if (!Actor.Bearer.IsUsable() || ExpiresAt <= _timeProvider.GetUtcNow() ||
            Registration.ExpiresAt <= _timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("remote_mcp_native_actor_expired");
    }
}
