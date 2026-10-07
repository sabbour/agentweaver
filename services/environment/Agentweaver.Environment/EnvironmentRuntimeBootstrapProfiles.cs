using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed record EnvironmentRuntimeBootstrapProfile(
    string ProfileId,
    Uri ConfigureEndpoint,
    Uri ObservationEndpoint)
{
    public EnvironmentRuntimeBootstrapProfile Validate()
    {
        if (!IsIdentifier(ProfileId) ||
            !RuntimeContractValidation.IsHttpsEndpoint(ConfigureEndpoint) ||
            !RuntimeContractValidation.IsHttpsEndpoint(ObservationEndpoint))
            throw new ArgumentException("The Environment Runtime bootstrap profile is invalid.");
        return this;
    }

    internal static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

public sealed record EnvironmentRuntimeBootstrapProfileRegistration(
    EnvironmentOwnerIdentity Owner,
    EnvironmentRuntimeBootstrapProfile Profile,
    ProviderResourceRef Placement)
{
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

public sealed class EnvironmentRuntimeBootstrapProfileRegistry
{
    private readonly ImmutableDictionary<OwnerProfileKey, EnvironmentRuntimeBootstrapProfileRegistration> _registrations;

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

    public EnvironmentRuntimeBootstrapProfile Resolve(
        EnvironmentOwnerIdentity owner,
        string profileId,
        ProviderResourceRef currentPlacement)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(currentPlacement);
        if (!EnvironmentRuntimeBootstrapProfile.IsIdentifier(profileId))
            throw Unavailable();

        var key = OwnerProfileKey.From(owner, profileId);
        if (!_registrations.TryGetValue(key, out var registration) ||
            registration.Placement != currentPlacement)
            throw Unavailable();
        return registration.Profile;
    }

    private static RuntimeAuthorizationException Unavailable() =>
        new("runtime_delivery_unavailable");

    private sealed record OwnerProfileKey(
        string TenantId,
        string ProjectId,
        string RunId,
        string EnvironmentId,
        string ProfileId)
    {
        public static OwnerProfileKey From(EnvironmentOwnerIdentity owner, string profileId) => new(
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            profileId);
    }
}
