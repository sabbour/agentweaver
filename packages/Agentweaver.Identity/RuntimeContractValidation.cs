using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Identity;

public static class RuntimeContractValidation
{
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
        if (binding.PlacementProviderId is { } placementProviderId)
        {
            ValidateIdentifier(placementProviderId);
            if (binding.EnvironmentLifecycleGeneration <= 0 || binding.EnvironmentLeaseRevision <= 0)
                throw new RuntimeAuthorizationException("runtime_binding_invalid");
        }
        else if (binding.EnvironmentLifecycleGeneration != 0 || binding.EnvironmentLeaseRevision != 0)
            throw new RuntimeAuthorizationException("runtime_binding_invalid");
        ValidateHash(binding.AcceptedSelectionHash);
    }

    public static void ValidateHash(string value)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new RuntimeAuthorizationException("runtime_hash_invalid");
    }

    public static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    public static string RegistrationHash(RuntimeRegistration registration) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(registration));

    public static bool IsHttpsEndpoint(Uri? endpoint) =>
        endpoint is { IsAbsoluteUri: true } &&
        endpoint.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        string.IsNullOrEmpty(endpoint.Query);

    public static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new RuntimeAuthorizationException("runtime_identifier_invalid");
    }

    public static bool VerifierMatches(string secret, string expectedVerifier)
    {
        ValidateHash(secret);
        ValidateHash(expectedVerifier);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.ASCII.GetBytes(secret)),
            Convert.FromHexString(expectedVerifier));
    }
}
