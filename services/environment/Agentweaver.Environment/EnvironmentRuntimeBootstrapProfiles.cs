using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Runtime host endpoints and optional MCP server pinned to one profile identifier.</summary>
public sealed record EnvironmentRuntimeBootstrapProfile(
    string ProfileId,
    Uri ConfigureEndpoint,
    Uri ObservationEndpoint)
{
    /// <summary>Validates the profile identifier and HTTPS endpoints before the profile can be used.</summary>
    public EnvironmentRuntimeBootstrapProfile Validate()
    {
        if (!IsIdentifier(ProfileId) ||
            !RuntimeContractValidation.IsHttpsEndpoint(ConfigureEndpoint) ||
            !RuntimeContractValidation.IsHttpsEndpoint(ObservationEndpoint) ||
            McpServer is { IsValid: false })
            throw new ArgumentException("The Environment Runtime bootstrap profile is invalid.");
        return this;
    }

    public RuntimeMcpServerEndpoint? McpServer { get; init; }

    /// <summary>Checks the bounded identifier alphabet shared by bootstrap profile IDs.</summary>
    internal static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

    /// <summary>Registration of a bootstrap profile for an exact Environment owner and Sandbox resource.</summary>
public sealed record EnvironmentRuntimeBootstrapProfileRegistration(
    EnvironmentOwnerIdentity Owner,
    EnvironmentRuntimeBootstrapProfile Profile,
    ProviderResourceRef Placement)
{
    /// <summary>Validates that the profile registration names an exact Sandbox placement.</summary>
    public EnvironmentRuntimeBootstrapProfileRegistration Validate()
    {
        ArgumentNullException.ThrowIfNull(Owner);
        ArgumentNullException.ThrowIfNull(Profile);
        ArgumentNullException.ThrowIfNull(Placement);
        _ = Profile.Validate();
        if (Placement.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(Placement.ProviderId) ||
            string.IsNullOrWhiteSpace(Placement.ResourceId) ||
            Placement.Generation < 1)
            throw new ArgumentException("The profile registration must identify an exact Sandbox placement.");
        return this;
    }
}

/// <summary>Owner-authorized profile replacement pinned to actor, selection, source profile, lease, and runtime endpoint.</summary>
public sealed record EnvironmentRuntimeBootstrapProfileReplacement(
    int ContractVersion,
    string SourceProfileId,
    string RequestedSourceProfileId,
    string ActorIssuer,
    string ActorId,
    string AcceptedSelectionHash,
    string SourceProfileConfigurationHash,
    string RegistrationProfileConfigurationHash,
    EnvironmentRuntimeBootstrapProfileRegistration Registration,
    long LifecycleGeneration,
    long CurrentFencingGeneration,
    long ProviderFencingGeneration,
    long LeaseRevision,
    DateTimeOffset LeaseExpiresAt,
    SandboxRuntimeHttpsEndpoint RuntimeEndpoint)
{
    /// <summary>Validates the replacement contract, configuration hashes, actor binding, endpoint, and lease fences.</summary>
    public EnvironmentRuntimeBootstrapProfileReplacement Validate()
    {
        ArgumentNullException.ThrowIfNull(Registration);
        ArgumentNullException.ThrowIfNull(RuntimeEndpoint);
        _ = Registration.Validate();
        if (ContractVersion != 1 ||
            !EnvironmentRuntimeBootstrapProfile.IsIdentifier(SourceProfileId) ||
            !EnvironmentRuntimeBootstrapProfile.IsIdentifier(RequestedSourceProfileId) ||
            SourceProfileId == Registration.Profile.ProfileId ||
            RequestedSourceProfileId == Registration.Profile.ProfileId ||
            !Uri.TryCreate(ActorIssuer, UriKind.Absolute, out var issuer) ||
            !RuntimeContractValidation.IsHttpsEndpoint(issuer) ||
            !Guid.TryParseExact(ActorId, "D", out _) ||
            !IsHash(AcceptedSelectionHash) ||
            !IsHash(SourceProfileConfigurationHash) ||
            !IsHash(RegistrationProfileConfigurationHash) ||
            RegistrationProfileConfigurationHash != ConfigurationHash(Registration.Profile) ||
            LifecycleGeneration <= 0 ||
            CurrentFencingGeneration <= 0 ||
            ProviderFencingGeneration != CurrentFencingGeneration ||
            LeaseRevision <= 0 ||
            LeaseExpiresAt <= DateTimeOffset.MinValue)
            throw new ArgumentException("The Runtime bootstrap profile replacement is invalid.");
        _ = RuntimeEndpoint.ValidateForConfigureEndpoint(Registration.Profile.ConfigureEndpoint);
        return this;
    }

    /// <summary>Checks whether this replacement was created for the same owner-context actor and accepted selection.</summary>
    internal bool MatchesOwnerContext(RuntimeOwnerContext ownerContext) =>
        ownerContext.ContractVersion == 1 &&
        ownerContext.TenantId == Registration.Owner.TenantId &&
        ownerContext.ProjectId == Registration.Owner.ProjectId &&
        ownerContext.RunId == Registration.Owner.RunId &&
        ownerContext.ActorIssuer == ActorIssuer &&
        ownerContext.ActorId == ActorId &&
        ownerContext.AcceptedSelectionHash == AcceptedSelectionHash;

    /// <summary>Checks that a runtime registration is bound to this replacement's owner, profile, actor, and selection.</summary>
    internal bool MatchesRuntimeBinding(RuntimeBinding binding) =>
        binding.TenantId == Registration.Owner.TenantId &&
        binding.ProjectId == Registration.Owner.ProjectId &&
        binding.RunId == Registration.Owner.RunId &&
        binding.EnvironmentId == Registration.Owner.EnvironmentId &&
        binding.ProfileId == Registration.Profile.ProfileId &&
        binding.ActorIssuer == ActorIssuer &&
        binding.ActorId == ActorId &&
        binding.AcceptedSelectionHash == AcceptedSelectionHash;

    /// <summary>Checks that a replacement derives from the exact stored source profile and requested profile identifier.</summary>
    internal bool MatchesSourceProfile(
        EnvironmentRuntimeBootstrapProfileRegistration source,
        string profileId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Owner != Registration.Owner ||
            source.Profile.ProfileId != SourceProfileId ||
            Registration.Profile.ProfileId != profileId ||
            SourceProfileConfigurationHash != ConfigurationHash(source.Profile) ||
            RegistrationProfileConfigurationHash != ConfigurationHash(Registration.Profile) ||
            RuntimeEndpoint.ReceiverConfigureEndpoint is not { } configureEndpoint)
            return false;
        var expectedProfile = source.Profile with
        {
            ProfileId = profileId,
            ConfigureEndpoint = configureEndpoint
        };
        return Registration.Profile == expectedProfile;
    }

    /// <summary>Compares every saved lifecycle and lease fence with the current Sandbox placement projection.</summary>
    public bool MatchesCurrentPlacement(EnvironmentSandboxPlacementProjectionV1 placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return Registration.Owner == new EnvironmentOwnerIdentity(
                placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId) &&
            Registration.Placement == placement.Resource &&
            LifecycleGeneration == placement.LifecycleGeneration &&
            CurrentFencingGeneration == placement.CurrentFencingGeneration &&
            ProviderFencingGeneration == placement.ProviderFencingGeneration &&
            LeaseRevision == placement.LeaseRevision &&
            LeaseExpiresAt == placement.LeaseExpiresAt;
    }

    /// <summary>Computes a stable hash of the validated profile configuration for replacement integrity checks.</summary>
    internal static string ConfigurationHash(EnvironmentRuntimeBootstrapProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = profile.Validate();
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(profile));
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Provides immutable profile registrations keyed by Environment owner and profile ID.</summary>
public sealed class EnvironmentRuntimeBootstrapProfileRegistry
{
    private readonly ImmutableDictionary<OwnerProfileKey, EnvironmentRuntimeBootstrapProfileRegistration> _registrations;

    /// <summary>Validates all profile registrations and rejects duplicate owner/profile keys.</summary>
    public EnvironmentRuntimeBootstrapProfileRegistry(
        IEnumerable<EnvironmentRuntimeBootstrapProfileRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var builder = ImmutableDictionary.CreateBuilder<OwnerProfileKey, EnvironmentRuntimeBootstrapProfileRegistration>();
        foreach (var unvalidated in registrations)
        {
            var registration = (unvalidated ?? throw new ArgumentException(
                "A Runtime bootstrap profile registration is required.", nameof(registrations))).Validate();
            var key = OwnerProfileKey.From(registration.Owner, registration.Profile.ProfileId);
            if (!builder.TryAdd(key, registration))
                throw new ArgumentException(
                    "A Runtime bootstrap profile is already registered for this Environment owner.",
                    nameof(registrations));
        }
        _registrations = builder.ToImmutable();
    }

    /// <summary>Returns the profile only when it is registered for the exact current placement.</summary>
    public EnvironmentRuntimeBootstrapProfile Resolve(
        EnvironmentOwnerIdentity owner,
        string profileId,
        ProviderResourceRef currentPlacement)
    {
        if (!TryResolve(owner, profileId, currentPlacement, out var registration))
            throw Unavailable();
        return registration!.Profile;
    }

    /// <summary>Looks up a profile bound to the exact Environment owner and Sandbox placement.</summary>
    public bool TryResolve(
        EnvironmentOwnerIdentity owner,
        string profileId,
        ProviderResourceRef currentPlacement,
        out EnvironmentRuntimeBootstrapProfileRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(currentPlacement);
        if (!EnvironmentRuntimeBootstrapProfile.IsIdentifier(profileId))
        {
            registration = null;
            return false;
        }

        var key = OwnerProfileKey.From(owner, profileId);
        if (_registrations.TryGetValue(key, out var found) && found.Placement == currentPlacement)
        {
            registration = found;
            return true;
        }
        registration = null;
        return false;
    }

    /// <summary>Looks up a source profile by owner and ID without requiring the target placement to match.</summary>
    public bool TryResolveSource(
        EnvironmentOwnerIdentity owner,
        string profileId,
        out EnvironmentRuntimeBootstrapProfileRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!EnvironmentRuntimeBootstrapProfile.IsIdentifier(profileId))
        {
            registration = null;
            return false;
        }
        return _registrations.TryGetValue(OwnerProfileKey.From(owner, profileId), out registration);
    }

    private static RuntimeAuthorizationException Unavailable() =>
        new("runtime_delivery_unavailable");

    /// <summary>Forms the immutable lookup key from all owner fields and the profile ID.</summary>
    private sealed record OwnerProfileKey(
        string TenantId,
        string ProjectId,
        string RunId,
        string EnvironmentId,
        string ProfileId)
    {
        /// <summary>Creates a profile lookup key from the complete owner identity and profile ID.</summary>
        public static OwnerProfileKey From(EnvironmentOwnerIdentity owner, string profileId) => new(
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            profileId);
    }
}
