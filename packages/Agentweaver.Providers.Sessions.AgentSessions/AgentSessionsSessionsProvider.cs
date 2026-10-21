using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sessions.AgentSessions;

/// <summary>Configures one AgentSessions record resource for the Sessions provider catalog.</summary>
/// <param name="ResourceId">Stable identity of the verified export set that serves the run.</param>
/// <param name="ResourceGeneration">Positive generation recorded when the resource is pinned.</param>
/// <param name="OptionsRevision">Provider configuration revision that must match the catalog entry.</param>
/// <param name="OptionsSchemaVersion">Options contract version. It must be 1.</param>
public sealed record AgentSessionsProviderOptions(
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion = 1)
{
    /// <summary>Requires token-shaped identifiers, a positive generation, and options schema version 1.</summary>
    /// <exception cref="ArgumentException">One or more option values are invalid.</exception>
    public void Validate()
    {
        if (!IsToken(ResourceId, 256) || ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) || OptionsSchemaVersion != 1)
            throw new ArgumentException("AgentSessions provider options are invalid.");
    }

    private static bool IsToken(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

/// <summary>
/// Optional Sessions provider that serves verified AgentSessions record exports through
/// <see cref="ISessionsJournal"/>. The native PostgreSQL provider stays the platform default. A host
/// adds this provider's registration to its catalog only when the package is deployed.
/// </summary>
public sealed class AgentSessionsSessionsProvider
{
    /// <summary>Catalog identity of this provider.</summary>
    public const string ProviderId = "agentsessions.records";

    /// <summary>Options contract version this adapter accepts.</summary>
    public const int OptionsSchemaVersion = 1;

    /// <summary>
    /// Sessions adapter contract version recorded in every run binding. The resolver matches it exactly, so
    /// it equals the version the Events &amp; Sessions host requests for the v1 Sessions contract.
    /// </summary>
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    /// <summary>
    /// Capabilities of the read-only record journal. Append and fork are absent, so a run that requires
    /// them can't negotiate this provider.
    /// </summary>
    public static ImmutableHashSet<string> Capabilities { get; } = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        SessionsCapabilities.Replay,
        SessionsCapabilities.Subscribe,
        SessionsCapabilities.ObjectReferences,
        SessionsCapabilities.ToolCalls);

    /// <summary>Catalog descriptor. The journal runs in the host process.</summary>
    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Sessions,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.InProcess,
        Capabilities);

    /// <summary>Creates an enabled catalog registration for these validated options.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ProviderRegistration CreateRegistration(AgentSessionsProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ProviderRegistration(Descriptor, Enabled: true, options.OptionsRevision, OptionsSchemaVersion);
    }

    /// <summary>Resolves a candidate, negotiates it against the options, and pins it to a run.</summary>
    /// <returns>The immutable run binding, or a typed resolution or negotiation failure.</returns>
    public ProviderResult<PinnedProviderBinding> ResolveNegotiateAndPin(
        ProviderResolver resolver,
        ProviderResolutionRequest request,
        AgentSessionsProviderOptions options,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var resolution = resolver.Resolve(request);
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate)
            return ProviderResult<PinnedProviderBinding>.Failure(
                resolution.Error?.Code ?? ProviderErrorCode.MissingDefault,
                resolution.Error?.Message ?? "A Sessions provider is required.");

        var negotiation = Negotiate(candidate, options);
        return negotiation.IsSuccess
            ? resolver.Pin(runId, candidate, options.ResourceId, negotiation.Value!)
            : ProviderResult<PinnedProviderBinding>.Failure(negotiation.Error!.Code, negotiation.Error.Message);
    }

    /// <summary>Checks that the candidate is this provider, with matching options and supported capabilities.</summary>
    /// <returns>The negotiated resource, or <see cref="ProviderErrorCode.InvalidConfiguration"/>.</returns>
    public ProviderResult<ResourceNegotiation> Negotiate(ProviderCandidate candidate, AgentSessionsProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (candidate.Seam != ProviderSeam.Sessions || candidate.ProviderId != ProviderId ||
            candidate.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            candidate.OptionsRevision != options.OptionsRevision ||
            !candidate.RequiredCapabilities.IsSubsetOf(Capabilities))
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.InvalidConfiguration,
                "The Sessions candidate does not match the AgentSessions provider options.");

        return ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
            new ProviderResourceRef(ProviderSeam.Sessions, ProviderId, options.ResourceId, options.ResourceGeneration),
            candidate.RequiredCapabilities));
    }

    /// <summary>Converts a pinned run binding into the Sessions contract binding for one project.</summary>
    /// <exception cref="ArgumentException">The binding isn't a Sessions binding for this provider.</exception>
    public static SessionProviderBinding ToSessionBinding(string projectId, PinnedProviderBinding pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        if (pinned.Seam != ProviderSeam.Sessions || pinned.ProviderId != ProviderId)
            throw new ArgumentException("The pinned binding isn't an AgentSessions Sessions binding.", nameof(pinned));
        return new SessionProviderBinding(
            projectId, pinned.RunId, pinned.ProviderId, pinned.AdapterVersion, pinned.OptionsSchemaVersion,
            pinned.OptionsRevision, pinned.Resource.ResourceId, pinned.Resource.Generation,
            pinned.NegotiatedCapabilities);
    }
}
