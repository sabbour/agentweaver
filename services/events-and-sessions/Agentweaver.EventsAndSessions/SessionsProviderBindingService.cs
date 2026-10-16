using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Npgsql;

namespace Agentweaver.EventsAndSessions;

/// <summary>Resolves a Sessions provider for a run and verifies its immutable saved binding.</summary>
public interface ISessionsProviderBinder
{
    /// <summary>Returns the run's existing provider pin or negotiates and pins the configured provider.</summary>
    /// <param name="principal">The caller whose project/run claims define the binding scope.</param>
    /// <param name="cancellationToken">Cancels database negotiation or provider checks.</param>
    /// <returns>The exact persisted or newly negotiated provider binding.</returns>
    Task<SessionProviderBinding> ResolveAndPinAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default);

    /// <summary>Checks the saved binding against the exact provider, options, resource, and capabilities still available.</summary>
    Task VerifyPinnedAsync(
        ClaimsPrincipal principal,
        SessionProviderBinding binding,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the native PostgreSQL Sessions adapter and provider catalog to keep one provider identity fixed per run.
/// </summary>
public sealed class SessionsProviderBindingService : ISessionsProviderBinder
{
    private readonly NativePostgresSessionsProvider _provider;
    private readonly ProviderCatalog _catalog;
    private readonly ProviderResolver _resolver;
    private readonly PostgresSessionsProviderOptions _options;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISessionsJournal _journal;
    private readonly IReadOnlyDictionary<string, string> _projectOverrides;

    public SessionsProviderBindingService(
        NativePostgresSessionsProvider provider,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        PostgresSessionsProviderOptions options,
        NpgsqlDataSource dataSource,
        ISessionsJournal journal,
        IReadOnlyDictionary<string, string>? projectOverrides = null)
    {
        _provider = provider;
        _catalog = catalog;
        _resolver = resolver;
        _options = options;
        _dataSource = dataSource;
        _journal = journal;
        _projectOverrides = projectOverrides ?? ImmutableDictionary<string, string>.Empty;
    }

    public async Task<SessionProviderBinding> ResolveAndPinAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (!SessionIdentityClaims.TryGetScope(principal, out var scope) || scope is null)
            throw new SessionAuthenticationException();
        var runScope = scope.Value;
        try
        {
            // An existing run is resolved only through its recorded provider, never a changed platform default.
            var pinned = await _journal.GetRunProviderBindingAsync(
                principal, runScope.ProjectId, runScope.RunId, cancellationToken).ConfigureAwait(false);
            await VerifyPinnedAsync(principal, pinned, cancellationToken).ConfigureAwait(false);
            return pinned;
        }
        catch (SessionNotFoundException)
        {
        }

        _projectOverrides.TryGetValue(runScope.ProjectId, out var overrideId);
        var result = await _provider.ResolveNegotiateAndPinAsync(
            _resolver,
            new ProviderResolutionRequest(
                ProviderSeam.Sessions,
                overrideId,
                NativePostgresSessionsProvider.AdapterVersion,
                NativePostgresSessionsProvider.OptionsSchemaVersion,
                SessionsCapabilities.All),
            _options,
            _dataSource,
            _options.ResourceId,
            runScope.RunId,
            cancellationToken);
        if (!result.IsSuccess || result.Value is null)
            throw new SessionsProviderUnavailableException(result.Error?.Message
                ?? "The configured Sessions provider could not be selected.");
        return ToContractBinding(runScope.ProjectId, result.Value);
    }

    public async Task VerifyPinnedAsync(
        ClaimsPrincipal principal,
        SessionProviderBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!SessionIdentityClaims.TryGetScope(principal, out var scope) || scope is null)
            throw new SessionAuthenticationException();
        var runScope = scope.Value;
        if (binding.ProjectId != runScope.ProjectId || binding.RunId != runScope.RunId)
            throw new SessionAccessDeniedException("The pinned provider belongs to a different project or run.");

        if (!_catalog.TryGetProvider(binding.ProviderId, out var registration) ||
            registration is null ||
            registration.Descriptor.Seam != ProviderSeam.Sessions)
            throw new SessionPinnedProviderUnavailableException(
                "The exact pinned Sessions provider is no longer available.");

        // Resolve from a catalog whose only default is the persisted provider. A changed
        // platform default must never substitute a different provider for an existing run.
        var exactCatalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sessions, binding.ProviderId)],
            []);
        if (!exactCatalog.IsSuccess || exactCatalog.Value is null)
            throw new SessionPinnedProviderUnavailableException(
                "The exact pinned Sessions provider configuration is no longer valid.");

        var exactResolver = new ProviderResolver(exactCatalog.Value);
        var exactRequest = new ProviderResolutionRequest(
            ProviderSeam.Sessions,
            null,
            binding.AdapterVersion,
            binding.OptionsSchemaVersion,
            binding.NegotiatedCapabilities);
        var result = await _provider.ResolveNegotiateAndPinAsync(
            exactResolver,
            exactRequest,
            _options,
            _dataSource,
            binding.ResourceId,
            runScope.RunId,
            cancellationToken);
        if (!result.IsSuccess || result.Value is null)
            throw new SessionPinnedProviderUnavailableException(
                "The exact pinned Sessions provider, options, or resource is no longer available.");

        var current = ToContractBinding(runScope.ProjectId, result.Value);
        if (!binding.Matches(current))
            throw new SessionPinnedProviderUnavailableException(
                "The exact pinned Sessions provider binding has changed.");
    }

    /// <summary>Copies the provider resolver's negotiated identity into the journal contract.</summary>
    private static SessionProviderBinding ToContractBinding(
        string projectId,
        PinnedProviderBinding binding) =>
        new(
            projectId,
            binding.RunId,
            binding.ProviderId,
            binding.AdapterVersion,
            binding.OptionsSchemaVersion,
            binding.OptionsRevision,
            binding.Resource.ResourceId,
            binding.Resource.Generation,
            binding.NegotiatedCapabilities);
}

/// <summary>Signals that the configured Sessions provider could not be resolved for a run.</summary>
public sealed class SessionsProviderUnavailableException(string message) : Exception(message);
