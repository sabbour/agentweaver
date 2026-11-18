using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Shared fail-closed validators and hashing helpers for runtime identity contracts.</summary>
public static class RuntimeContractValidation
{
    /// <exception cref="RuntimeAuthorizationException">Any registration field is invalid or inconsistent.</exception>
    public static void Validate(RuntimeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var binding = registration.Binding
            ?? throw new RuntimeAuthorizationException("runtime_binding_invalid");
        if (registration.RuntimeInstanceId == Guid.Empty || registration.Revision <= 0 ||
            !Enum.IsDefined(registration.State) ||
            binding.ProjectRevision <= 0 || binding.ProjectConfigurationRevision <= 0 ||
            binding.PlatformRuntimeRevision <= 0 || binding.ExecutionFence <= 0 ||
            binding.PlacementGeneration <= 0 ||
            binding.EnvironmentCurrentFencingGeneration <= 0 ||
            binding.EnvironmentProviderFencingGeneration <= 0 ||
            !Guid.TryParseExact(binding.ActorId, "D", out _) ||
            !Uri.TryCreate(binding.ActorIssuer, UriKind.Absolute, out var issuer) ||
            !IsHttpsEndpoint(issuer) ||
            !IsHttpsEndpoint(binding.ConfigureEndpoint) ||
            !IsHttpsEndpoint(binding.ObservationEndpoint))
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        foreach (var value in new[]
        {
            binding.TenantId, binding.ProjectId, binding.RunId, binding.SessionId,
            binding.AgentId, binding.TurnId, binding.ContextRevision,
            binding.EnvironmentId, binding.PlacementUid, binding.ProfileId
        })
            ValidateIdentifier(value);
        if (binding.ModelSelectionReference is { } modelSelectionReference)
            ValidateIdentifier(modelSelectionReference);
        if (binding.WorkflowStepId is { } workflowStepId)
        {
            ValidateIdentifier(workflowStepId);
            if (workflowStepId.Length > 128)
                throw new RuntimeAuthorizationException("runtime_binding_invalid");
        }
        if (binding.ModelSourceMode is { } modelSourceMode &&
            (!Enum.IsDefined(modelSourceMode) || binding.ModelSelectionReference is null))
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        if (!ModelReasoningEfforts.IsSupported(binding.ReasoningEffort))
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        if (binding.ModelBindingPin is { } modelBindingPin)
        {
            Agentweaver.AgentRuntime.RuntimeModelBindingsResolver.ValidatePin(modelBindingPin);
            if (modelBindingPin.ModelSelectionReference != binding.ModelSelectionReference ||
                modelBindingPin.SourceMode != binding.ModelSourceMode ||
                modelBindingPin.ReasoningEffort != binding.ReasoningEffort)
                throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");
        }
        if (binding.ModelCredentialReference is { } modelCredentialReference)
        {
            ValidateIdentifier(modelCredentialReference.Id);
            ValidateIdentifier(modelCredentialReference.Version);
            if (binding.ModelSelectionReference is null)
                throw new RuntimeAuthorizationException("runtime_binding_invalid");
        }
        if (binding.ModelCredentialIdentity is { } credentialIdentity)
        {
            ValidateModelCredentialIdentity(credentialIdentity);
            if (binding.ModelSourceMode != ModelSourceMode.Byok ||
                binding.ModelCredentialReference is null ||
                binding.ModelSelectionReference != credentialIdentity.ProviderId ||
                binding.ModelBindingPin?.ProviderId != credentialIdentity.ProviderId ||
                credentialIdentity.Scope == ModelProviderBindingScope.Project &&
                    credentialIdentity.ProjectId != binding.ProjectId)
                throw new RuntimeAuthorizationException("runtime_model_credential_identity_invalid");
        }
        else if (binding.ModelBindingPin?.ProviderId is not null)
            throw new RuntimeAuthorizationException("runtime_model_credential_identity_invalid");
        if (binding.ProviderBindingRevision is { } providerBinding)
        {
            Agentweaver.AgentRuntime.RuntimeModelBindingsResolver.ValidateProviderBinding(providerBinding);
            if (binding.ModelSourceMode != ModelSourceMode.Byok ||
                binding.ModelCredentialReference?.Version != providerBinding.CredentialVersion ||
                binding.ModelCredentialReference != providerBinding.CredentialReference ||
                binding.ModelCredentialIdentity != providerBinding.CredentialIdentity ||
                binding.RuntimeRateCardAdmission is not { } rateCardAdmission ||
                !Agentweaver.AgentRuntime.RuntimeModelBindingsResolver.ProviderBindingsEqual(
                    rateCardAdmission.ProviderBindingRevision, providerBinding) ||
                rateCardAdmission.ModelBindingPin != binding.ModelBindingPin ||
                rateCardAdmission.CostBinding.RuntimeByokIdentity !=
                    RuntimeByokCostBindingIdentity.From(binding.ModelBindingPin!, providerBinding) ||
                rateCardAdmission.CostBinding.MeterSource != SdkMeterSources.ByokTokens ||
                providerBinding.RateCardPin is not { } providerRateCardPin ||
                rateCardAdmission.CostBinding.RuntimeRateCardPin is not { } admittedRateCardPin ||
                admittedRateCardPin.RateCardId != providerRateCardPin.RateCardId ||
                admittedRateCardPin.RateCardVersion != providerRateCardPin.RateCardVersion ||
                admittedRateCardPin.ContentHash != providerRateCardPin.ContentHash ||
                !RuntimeRateCardReadContract.MatchesPin(
                    new(RuntimeRateCardReadContract.CurrentSchemaVersion,
                        rateCardAdmission.CostBinding.RateCard, admittedRateCardPin.Provenance),
                    providerRateCardPin) ||
                binding.ModelSelectionReference != providerBinding.ProviderId ||
                binding.ModelBindingPin != Agentweaver.AgentRuntime.RuntimeModelBindingsResolver.Pin(
                    providerBinding, binding.ReasoningEffort))
                throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");
        }
        else if (binding.ModelCredentialIdentity is not null || binding.ModelBindingPin?.ProviderId is not null ||
                 binding.RuntimeRateCardAdmission is not null)
            throw new RuntimeAuthorizationException("runtime_model_binding_revision_required");
        if (binding.ModelConnectionId is { } connectionId &&
            (connectionId == Guid.Empty || binding.ModelSourceMode != Agentweaver.Abstractions.ModelSourceMode.HostedCopilot ||
                binding.ModelCredentialReference is not null ||
                binding.ModelConnectionScope is not (Agentweaver.Abstractions.ProjectAuthorityResourceType.Project or
                    Agentweaver.Abstractions.ProjectAuthorityResourceType.Platform)) ||
            binding.ModelConnectionId is null && binding.ModelConnectionScope is not null)
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        if (binding.MaxModelTurns is { } maxModelTurns &&
                (maxModelTurns < 1 || maxModelTurns > 1000) ||
            binding.MaxToolCalls is { } maxToolCalls &&
                (maxToolCalls < 1 || maxToolCalls > 10000) ||
            binding.MaxPromptTokens is { } maxPromptTokens &&
                (maxPromptTokens < 1024 || maxPromptTokens > 200000) ||
            binding.MaxRevisionAttempts is < 0 ||
            binding.CopilotSoftCreditLimit is < 0 ||
            binding.CopilotHardCreditLimit is < 0 ||
            binding.CopilotSoftCreditLimit is { } softLimit &&
                binding.CopilotHardCreditLimit is { } hardLimit && softLimit > hardLimit)
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        if (binding.PlacementProviderId is { } placementProviderId)
        {
            ValidateIdentifier(placementProviderId);
            if (binding.EnvironmentLifecycleGeneration <= 0 || binding.EnvironmentLeaseRevision <= 0)
                throw new RuntimeAuthorizationException("runtime_binding_invalid");
        }
        else if (binding.EnvironmentLifecycleGeneration != 0 || binding.EnvironmentLeaseRevision != 0)
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        if (binding.ResumeOperationId.HasValue != binding.ResumeAttemptId.HasValue ||
            binding.ResumeOperationId == Guid.Empty || binding.ResumeAttemptId == Guid.Empty ||
            binding.ResumeOperationId.HasValue && binding.PlacementProviderId is null)
            throw new RuntimeAuthorizationException("runtime_resume_binding_invalid");
        if (binding.Image is { } image)
        {
            try
            {
                image.Validate();
            }
            catch (ArgumentException)
            {
                throw new RuntimeAuthorizationException("runtime_image_invalid");
            }
        }
        ValidateHash(binding.AcceptedSelectionHash);
    }

    /// <summary>Rejects model/tool numeric limits when the binding is not scoped to work or the coordinator root.</summary>
    public static void RequireSupportedNumericRunLimits(RuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.WorkflowStepId is null && binding.AgentId != RuntimeActionContract.CoordinatorRootStepId &&
            (binding.MaxModelTurns is not null || binding.MaxToolCalls is not null))
            throw new RuntimeAuthorizationException("runtime_numeric_limits_unsupported");
    }

    /// <summary>Requires a lowercase 64-character SHA-256 hex digest.</summary>
    public static void ValidateHash(string value)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new RuntimeAuthorizationException("runtime_hash_invalid");
    }

    public static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    /// <summary>Derives a stable opaque SDK session ID from tenant, project, run, and session scope.</summary>
    public static string NativeSessionId(RuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        foreach (var value in new[] { binding.TenantId, binding.ProjectId, binding.RunId, binding.SessionId })
            ValidateIdentifier(value);
        return "agentweaver-session-" + Hash(Encoding.UTF8.GetBytes(
            $"{binding.TenantId}\0{binding.ProjectId}\0{binding.RunId}\0{binding.SessionId}"));
    }

    /// <summary>Hashes the serialized registration for exact receipt binding.</summary>
    public static string RegistrationHash(RuntimeRegistration registration) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(registration));

    /// <summary>
    /// Returns true only for an absolute HTTPS URI with no user info, query, or fragment; false otherwise.
    /// </summary>
    public static bool IsHttpsEndpoint(Uri? endpoint) =>
        endpoint is { IsAbsoluteUri: true } &&
        endpoint.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        string.IsNullOrEmpty(endpoint.Query);

    /// <summary>Requires an opaque identifier containing only the shared ASCII character set.</summary>
    public static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new RuntimeAuthorizationException("runtime_identifier_invalid");
    }

    /// <summary>Requires a complete slot, scope, project, and provider identity.</summary>
    public static void ValidateModelCredentialIdentity(ModelCredentialIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.CredentialSlotId == Guid.Empty ||
            !Enum.IsDefined(identity.Scope) ||
            identity.Scope == ModelProviderBindingScope.Platform && identity.ProjectId is not null ||
            identity.Scope == ModelProviderBindingScope.Project &&
                (string.IsNullOrEmpty(identity.ProjectId) || identity.ProjectId.Length > 32) ||
            string.IsNullOrEmpty(identity.ProviderId) || identity.ProviderId.Length > 128)
            throw new RuntimeAuthorizationException("runtime_model_credential_identity_invalid");
        ValidateIdentifier(identity.ProviderId);
        if (identity.Scope == ModelProviderBindingScope.Project)
            ValidateIdentifier(identity.ProjectId!);
    }

    /// <summary>
    /// Returns true when the SHA-256 digest of a valid secret equals the expected verifier, and false
    /// when the digests differ. Invalid digest inputs throw. The digest comparison uses fixed time.
    /// </summary>
    public static bool VerifierMatches(string secret, string expectedVerifier)
    {
        ValidateHash(secret);
        ValidateHash(expectedVerifier);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.ASCII.GetBytes(secret)),
            Convert.FromHexString(expectedVerifier));
    }
}
