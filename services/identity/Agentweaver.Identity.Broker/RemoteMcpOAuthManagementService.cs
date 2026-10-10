using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Identity.Broker;

public sealed record RemoteMcpOAuthManagementStatus(
    Guid ConnectionId,
    string ProjectId,
    long ConnectionRevision,
    long CredentialRevision,
    string State,
    long StoredConfigurationRevision,
    long? CurrentConfigurationRevision,
    bool CurrentConfigurationMatches,
    bool CredentialUseAvailable);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteMcpOAuthConnectionRegistrationRequest(
    string ProjectId,
    Guid ConnectionId,
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256,
    string IssuerUri,
    IReadOnlyList<string> Scopes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteMcpOAuthDisconnectRequest(
    long ExpectedConnectionRevision,
    long ExpectedCredentialRevision,
    long ExpectedConfigurationRevision,
    Guid IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteMcpOAuthRefreshRequest(
    long ExpectedConnectionRevision,
    long ExpectedCredentialRevision,
    long ExpectedConfigurationRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteMcpOAuthConsentPreparationRequest(
    long ExpectedConnectionRevision,
    long ExpectedCredentialRevision,
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteMcpOAuthCallbackRequest(
    string State,
    string? Code,
    string? Error);

internal sealed record RemoteMcpOAuthConsentPreparation(
    Guid CorrelationId,
    string State,
    string PkceChallenge,
    Uri AuthorizationUri,
    DateTimeOffset ExpiresAt,
    long ConnectionRevision,
    long ConfigurationRevision,
    string ConfigurationSha256)
{
    public override string ToString() => nameof(RemoteMcpOAuthConsentPreparation) + " [REDACTED]";
}

internal sealed class RemoteMcpOAuthManagementException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed class RemoteMcpOAuthManagementService(
    IdentityBrokerDbContext db,
    RemoteMcpOAuthOptions options,
    HttpClient projects,
    HttpClient environment,
    HttpClient provider,
    TimeProvider time,
    ISecretVersionWriter? secretWriter = null,
    ISecretRedemption? secretRedemption = null)
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProviderRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshRecoveryAge = TimeSpan.FromMinutes(2);
    private const string OAuthAuthenticationMode = "delegatedOAuth";
    private const string TransportProfile = "streamableHttp20250618";
    private static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Uri _projectsOwnerAddress =
        RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(options.ProjectsOwnerAddress));
    private readonly Uri _environmentOwnerAddress =
        RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(options.EnvironmentOwnerAddress));
    private readonly ISecretRedemption? _secretRedemption = secretRedemption;

    public async Task<RemoteMcpOAuthManagementStatus> RegisterConnectionAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        RemoteMcpOAuthConnectionRegistrationRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ProjectId) || input.ProjectId.Length > 256 ||
            input.ProjectId.Any(char.IsControl) || input.ConnectionId == Guid.Empty ||
            input.ExpectedConfigurationRevision < 1 || !IsLowerHexHash(input.ExpectedConfigurationSha256) ||
            !IsCanonicalHttpsUri(input.IssuerUri) || input.Scopes is null ||
            input.Scopes.Count is 0 or > 64 || input.Scopes.Any(string.IsNullOrEmpty) ||
            input.Scopes.Distinct(StringComparer.Ordinal).Count() != input.Scopes.Count)
            throw InvalidRequest();

        var provider = options.Providers.SingleOrDefault(item =>
            string.Equals(item.IssuerUri, input.IssuerUri, StringComparison.Ordinal))
            ?? throw Denied("remote_mcp_oauth_provider_unapproved");
        var scopes = input.Scopes.Order(StringComparer.Ordinal).ToArray();
        if (scopes.Any(scope => !provider.ApprovedScopes.Contains(scope, StringComparer.Ordinal)))
            throw Denied("remote_mcp_oauth_scope_unapproved");

        var authority = await RequireProjectPermissionAsync(
            actor, issuer, actorId, input.ProjectId, expectedTenantId: null,
            ProjectAuthorizationPermission.WriteProjects, cancellationToken).ConfigureAwait(false);
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            user => user.Issuer == issuer && user.Subject == actorId && !user.Disabled,
            cancellationToken).ConfigureAwait(false)
            ?? throw NotFound();
        var current = await ReadCurrentConfigurationAsync(
            actor, input.ProjectId, input.ConnectionId, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound();
        if (current.ResourceUri is null ||
            !provider.ApprovedResources.Contains(current.ResourceUri, StringComparer.Ordinal))
            throw Denied("remote_mcp_oauth_resource_unapproved");
        if (!IsUnlinkedConfiguration(current, input))
            throw RevisionConflict();

        var scopesJson = JsonSerializer.Serialize(scopes, Json);
        var existing = await db.RemoteMcpOAuthConnections.AsNoTracking().SingleOrDefaultAsync(row =>
                row.OwnerId == owner.Id && row.TenantId == authority.TenantId &&
                row.ProjectId == input.ProjectId && row.ConnectionId == input.ConnectionId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.State == RemoteMcpOAuthConnectionState.NotConnected &&
                existing.ConfigurationRevision == input.ExpectedConfigurationRevision &&
                existing.EnvironmentConfigurationHash == input.ExpectedConfigurationSha256 &&
                existing.IssuerUri == provider.IssuerUri &&
                existing.ClientId == provider.ClientId &&
                existing.RedirectUri == provider.RedirectUri &&
                existing.ScopesJson == scopesJson &&
                IsExpectedPreLinkOrReplayConfiguration(existing, current))
                return ToStatus(existing, current);
            throw RevisionConflict();
        }

        var bindingReference = Guid.NewGuid().ToString("N");
        var binding = new RemoteMcpOAuthConnectionBinding(
            owner.Id.ToString("D"), authority.TenantId, input.ProjectId, input.ConnectionId,
            current.Revision, current.Hash, bindingReference, new Uri(current.EndpointUri),
            new Uri(current.ResourceUri!), new Uri(provider.IssuerUri), provider.ClientId,
            new Uri(provider.RedirectUri), RemoteMcpOAuthConnectionBinding.SupportedTransportProfile, scopes);
        var now = time.GetUtcNow();
        var row = new RemoteMcpOAuthConnectionRecord
        {
            Id = Guid.NewGuid(),
            OwnerId = owner.Id,
            OwnerIssuer = issuer,
            OwnerActorId = actorId,
            TenantId = authority.TenantId,
            ProjectId = input.ProjectId,
            ConnectionId = input.ConnectionId,
            ConfigurationRevision = current.Revision,
            EnvironmentConfigurationHash = current.Hash,
            IdentityBindingReference = bindingReference,
            EndpointUri = current.EndpointUri,
            ResourceUri = current.ResourceUri!,
            IssuerUri = provider.IssuerUri,
            ClientId = provider.ClientId,
            RedirectUri = provider.RedirectUri,
            TransportProfile = RemoteMcpOAuthConnectionBinding.SupportedTransportProfile,
            ScopesJson = scopesJson,
            BindingHash = binding.BindingHash,
            ConnectionRevision = 1,
            CredentialRevision = 0,
            State = RemoteMcpOAuthConnectionState.NotConnected,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await RequireProjectPermissionAsync(
                actor, issuer, actorId, row.ProjectId, row.TenantId,
                ProjectAuthorizationPermission.WriteProjects, cancellationToken).ConfigureAwait(false);
            var confirmed = await ReadCurrentConfigurationAsync(
                actor, row.ProjectId, row.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (confirmed != current || !IsUnlinkedConfiguration(confirmed, input))
                throw RevisionConflict();

            db.RemoteMcpOAuthConnections.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await RequireProjectPermissionAsync(
                actor, issuer, actorId, row.ProjectId, row.TenantId,
                ProjectAuthorizationPermission.WriteProjects, cancellationToken).ConfigureAwait(false);
            var final = await ReadCurrentConfigurationAsync(
                actor, row.ProjectId, row.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (final != current)
                throw RevisionConflict();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (
            error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw RevisionConflict();
        }

        return ToStatus(row, current);
    }

    public async Task<RemoteMcpOAuthManagementStatus> ReadStatusAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var row = await FindOwnedConnectionAsync(issuer, actorId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        await RequireReadProjectsAsync(actor, issuer, actorId, row, cancellationToken).ConfigureAwait(false);
        var currentConfiguration = await ReadCurrentConfigurationAsync(
            actor, row, cancellationToken).ConfigureAwait(false);
        await RequireReadProjectsAsync(actor, issuer, actorId, row, cancellationToken).ConfigureAwait(false);
        if (row.State == RemoteMcpOAuthConnectionState.RefreshInProgress &&
            row.RefreshStartedAt is { } refreshStartedAt &&
            refreshStartedAt <= time.GetUtcNow() - RefreshRecoveryAge)
        {
            row = await RecoverStaleRefreshAsync(row, cancellationToken).ConfigureAwait(false);
            await RequireReadProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            currentConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false);
            await RequireReadProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
        }
        return ToStatus(row, currentConfiguration);
    }

    public async Task<RemoteMcpOAuthManagementStatus> DisconnectAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        Guid connectionId,
        RemoteMcpOAuthDisconnectRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (connectionId == Guid.Empty || input.ExpectedConnectionRevision < 1 ||
            input.ExpectedCredentialRevision < 0 ||
            input.ExpectedConfigurationRevision < 1 || input.IdempotencyKey == Guid.Empty)
            throw InvalidRequest();

        var row = await FindOwnedConnectionAsync(issuer, actorId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        await RequireReadProjectsAsync(actor, issuer, actorId, row, cancellationToken).ConfigureAwait(false);
        var currentConfiguration = await ReadCurrentConfigurationAsync(
            actor, row, cancellationToken).ConfigureAwait(false)
            ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
        if (currentConfiguration.Revision != input.ExpectedConfigurationRevision)
            throw RevisionConflict();

        var idempotencyHash = Hash(input.IdempotencyKey.ToString("N"));
        var requestHash = DisconnectRequestHash(row, input, currentConfiguration);
        if (IsSameDisconnect(row, idempotencyHash, requestHash))
            return ToStatus(row, currentConfiguration);
        if (row.ConnectionRevision != input.ExpectedConnectionRevision ||
            row.CredentialRevision != input.ExpectedCredentialRevision)
            throw RevisionConflict();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var updated = await db.RemoteMcpOAuthConnections
            .Where(item =>
                item.Id == row.Id &&
                item.OwnerId == row.OwnerId &&
                item.OwnerIssuer == issuer &&
                item.OwnerActorId == actorId &&
                item.TenantId == row.TenantId &&
                item.ProjectId == row.ProjectId &&
                item.ConnectionId == connectionId &&
                item.ConfigurationRevision == row.ConfigurationRevision &&
                item.BindingHash == row.BindingHash &&
                item.ConnectionRevision == input.ExpectedConnectionRevision &&
                item.CredentialRevision == input.ExpectedCredentialRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, RemoteMcpOAuthConnectionState.Disconnected)
                .SetProperty(item => item.ConnectionRevision, item => item.ConnectionRevision + 1)
                .SetProperty(item => item.CredentialRevision, item => item.CredentialRevision + 1)
                .SetProperty(item => item.AccessTokenSecretId, (string?)null)
                .SetProperty(item => item.AccessTokenSecretVersion, (string?)null)
                .SetProperty(item => item.AccessTokenExpiresAt, (DateTimeOffset?)null)
                .SetProperty(item => item.RefreshTokenSecretId, (string?)null)
                .SetProperty(item => item.RefreshTokenSecretVersion, (string?)null)
                .SetProperty(item => item.RefreshTokenExpiresAt, (DateTimeOffset?)null)
                .SetProperty(item => item.RefreshAttemptId, (Guid?)null)
                .SetProperty(item => item.RefreshStartedAt, (DateTimeOffset?)null)
                .SetProperty(item => item.LastDisconnectIdempotencyHash, idempotencyHash)
                .SetProperty(item => item.LastDisconnectRequestHash, requestHash)
                .SetProperty(item => item.LastDisconnectResultRevision, input.ExpectedConnectionRevision + 1)
                .SetProperty(item => item.UpdatedAt, time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

        var latest = await db.RemoteMcpOAuthConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound();
        if (updated == 0 && !IsSameDisconnect(latest, idempotencyHash, requestHash))
            throw RevisionConflict();
        if (updated > 1)
            throw new InvalidOperationException("Remote MCP disconnect updated more than one connection.");

        await RequireReadProjectsAsync(actor, issuer, actorId, row, cancellationToken).ConfigureAwait(false);
        var latestConfiguration = await ReadCurrentConfigurationAsync(
            actor, row, cancellationToken).ConfigureAwait(false);
        if (latestConfiguration != currentConfiguration)
            throw RevisionConflict();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToStatus(latest, latestConfiguration);
    }

    public async Task<RemoteMcpOAuthManagementStatus> RefreshAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        Guid connectionId,
        RemoteMcpOAuthRefreshRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (connectionId == Guid.Empty || input.ExpectedConnectionRevision < 1 ||
            input.ExpectedCredentialRevision < 0 || input.ExpectedConfigurationRevision < 1)
            throw InvalidRequest();
        if (secretWriter is null || _secretRedemption is null)
            throw Unavailable("remote_mcp_secret_store_unavailable");

        var row = await FindOwnedConnectionAsync(issuer, actorId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
            .ConfigureAwait(false);
        if (row.State == RemoteMcpOAuthConnectionState.RefreshInProgress &&
            row.RefreshStartedAt is { } refreshStartedAt &&
            refreshStartedAt <= time.GetUtcNow() - RefreshRecoveryAge)
        {
            row = await RecoverStaleRefreshAsync(row, cancellationToken).ConfigureAwait(false);
            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var recoveredConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false);
            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            return ToStatus(row, recoveredConfiguration);
        }

        if (row.ConnectionRevision != input.ExpectedConnectionRevision ||
            row.CredentialRevision != input.ExpectedCredentialRevision ||
            row.ConfigurationRevision != input.ExpectedConfigurationRevision ||
            row.State != RemoteMcpOAuthConnectionState.Authorized)
            throw RevisionConflict();

        var currentConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
        if (!IsCurrentConfiguration(row, currentConfiguration))
            throw RevisionConflict();

        var binding = CreateBinding(
            row, row.ConfigurationRevision, row.EnvironmentConfigurationHash, row.IdentityBindingReference);
        var providerOptions = RequireBoundProvider(binding);
        var metadata = await DiscoverMetadataAsync(binding, providerOptions, cancellationToken)
            .ConfigureAwait(false);
        if (!metadata.SupportsRefreshToken)
            throw new RemoteMcpOAuthManagementException(
                "remote_mcp_oauth_refresh_unsupported", StatusCodes.Status409Conflict);

        await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
            .ConfigureAwait(false);
        var beforeClaim = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
        if (!IsCurrentConfiguration(row, beforeClaim))
            throw RevisionConflict();

        var connection = ToLifecycleConnection(row, binding);
        var attemptId = Guid.NewGuid();
        var claimedAt = time.GetUtcNow();
        var claim = RemoteMcpOAuthLifecycle.TryBeginRefresh(
            connection, binding, input.ExpectedConnectionRevision, attemptId, claimedAt,
            out var claimedConnection)
            ?? throw RevisionConflict();
        var claimed = claimedConnection ?? throw RevisionConflict();
        await ClaimRefreshAsync(row, connection, claimed, claim, claimedAt, cancellationToken)
            .ConfigureAwait(false);

        var requestMayHaveBeenSent = false;
        SecretCredential? refreshToken = null;
        SecretCredential? accessToken = null;
        SecretCredential? rotatedRefreshToken = null;
        try
        {
            refreshToken = await _secretRedemption.RedeemAsync(
                new SecretRedemptionRequest(
                    claim.RefreshTokenReference,
                    "remote-mcp-oauth-refresh-token",
                    $"{row.ConnectionId:N}:{claim.CredentialRevision}"),
                cancellationToken).ConfigureAwait(false);

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var beforeExchange = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, beforeExchange))
                throw RevisionConflict();

            var tokens = await ExchangeRefreshTokenAsync(
                metadata.TokenEndpoint.AbsoluteUri, binding, refreshToken,
                () => requestMayHaveBeenSent = true, cancellationToken).ConfigureAwait(false);
            accessToken = new SecretCredential(tokens.AccessToken, tokens.AccessTokenExpiresAt, time);
            if (tokens.RefreshToken is not null)
                rotatedRefreshToken = new SecretCredential(
                    tokens.RefreshToken, tokens.RefreshTokenExpiresAt ?? DateTimeOffset.MaxValue, time);

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var afterExchange = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, afterExchange))
                throw RevisionConflict();

            var accessSecretId = $"remote-mcp-{row.ConnectionId:N}-access";
            var accessReference = await secretWriter.WriteVersionAsync(
                accessSecretId, accessToken, cancellationToken).ConfigureAwait(false);
            if (accessReference.Id != accessSecretId)
                throw Unavailable("remote_mcp_secret_store_unavailable");
            SecretRef? refreshReference = null;
            if (rotatedRefreshToken is not null)
            {
                var refreshSecretId = $"remote-mcp-{row.ConnectionId:N}-refresh";
                refreshReference = await secretWriter.WriteVersionAsync(
                    refreshSecretId, rotatedRefreshToken, cancellationToken).ConfigureAwait(false);
                if (refreshReference.Id != refreshSecretId)
                    throw Unavailable("remote_mcp_secret_store_unavailable");
            }

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var beforeCommit = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, beforeCommit))
                throw RevisionConflict();

            var completed = RemoteMcpOAuthLifecycle.TryCompleteRefresh(
                claimed, binding, claim, claimed.Revision,
                accessReference, refreshReference, tokens.AccessTokenExpiresAt, time.GetUtcNow(),
                tokens.RefreshTokenExpiresAt)
                ?? throw RevisionConflict();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var updated = await db.RemoteMcpOAuthConnections
                .Where(item =>
                    item.Id == row.Id &&
                    item.OwnerId == row.OwnerId &&
                    item.OwnerIssuer == issuer &&
                    item.OwnerActorId == actorId &&
                    item.TenantId == row.TenantId &&
                    item.ProjectId == row.ProjectId &&
                    item.ConnectionId == row.ConnectionId &&
                    item.ConfigurationRevision == row.ConfigurationRevision &&
                    item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                    item.IdentityBindingReference == row.IdentityBindingReference &&
                    item.BindingHash == binding.BindingHash &&
                    item.ConnectionRevision == claim.ConnectionRevision &&
                    item.CredentialRevision == claim.CredentialRevision &&
                    item.State == RemoteMcpOAuthConnectionState.RefreshInProgress &&
                    item.RefreshAttemptId == claim.AttemptId &&
                    item.RefreshTokenSecretId == claim.RefreshTokenReference.Id &&
                    item.RefreshTokenSecretVersion == claim.RefreshTokenReference.Version)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, completed.State)
                    .SetProperty(item => item.ConnectionRevision, completed.Revision)
                    .SetProperty(item => item.CredentialRevision, completed.CredentialRevision)
                    .SetProperty(item => item.AccessTokenSecretId, completed.AccessTokenReference!.Id)
                    .SetProperty(item => item.AccessTokenSecretVersion, completed.AccessTokenReference!.Version)
                    .SetProperty(item => item.AccessTokenExpiresAt, completed.AccessTokenExpiresAt)
                    .SetProperty(item => item.RefreshTokenSecretId,
                        completed.RefreshTokenReference == null ? null : completed.RefreshTokenReference.Id)
                    .SetProperty(item => item.RefreshTokenSecretVersion,
                        completed.RefreshTokenReference == null ? null : completed.RefreshTokenReference.Version)
                    .SetProperty(item => item.RefreshTokenExpiresAt, completed.RefreshTokenExpiresAt)
                    .SetProperty(item => item.RefreshAttemptId, (Guid?)null)
                    .SetProperty(item => item.RefreshStartedAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.UpdatedAt, time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            if (updated != 1)
                throw RevisionConflict();

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var finalConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, finalConfiguration))
                throw RevisionConflict();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var latest = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.Id == row.Id, cancellationToken).ConfigureAwait(false);
            return ToStatus(latest, finalConfiguration);
        }
        catch (RemoteMcpOAuthTokenExchangeException exchangeError)
        {
            await ResolveRefreshFailureAsync(
                row, binding, claimed, claim, exchangeError.Failure, CancellationToken.None)
                .ConfigureAwait(false);
            throw new RemoteMcpOAuthManagementException(
                exchangeError.Failure == RemoteMcpOAuthRequestFailure.ProviderRejected
                    ? "remote_mcp_oauth_refresh_rejected"
                    : "remote_mcp_oauth_refresh_uncertain",
                exchangeError.Failure == RemoteMcpOAuthRequestFailure.ProviderRejected
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status503ServiceUnavailable);
        }
        catch (RemoteMcpOAuthManagementException)
        {
            await ResolveRefreshFailureAsync(
                row, binding, claimed, claim,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (RuntimeAuthorizationException)
        {
            await ResolveRefreshFailureAsync(
                row, binding, claimed, claim,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException)
        {
            await ResolveRefreshFailureAsync(
                row, binding, claimed, claim,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw Unavailable("remote_mcp_oauth_provider_unavailable");
        }
        catch (OperationCanceledException)
        {
            await ResolveRefreshFailureAsync(
                row, binding, claimed, claim,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw Unavailable(requestMayHaveBeenSent
                ? "remote_mcp_oauth_refresh_uncertain"
                : "remote_mcp_oauth_provider_unavailable");
        }
        catch (AzureKeyVaultSecretException)
        {
            await ResolveRefreshFailureAsync(
                row, binding, claimed, claim,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw Unavailable("remote_mcp_secret_store_unavailable");
        }
        finally
        {
            refreshToken?.Invalidate();
            accessToken?.Invalidate();
            rotatedRefreshToken?.Invalidate();
        }
    }

    public async Task<RemoteMcpOAuthConsentPreparation> PrepareConsentAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        Guid connectionId,
        RemoteMcpOAuthConsentPreparationRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (connectionId == Guid.Empty || input.ExpectedConnectionRevision < 1 ||
            input.ExpectedCredentialRevision < 0 || input.ExpectedConfigurationRevision < 1 ||
            !IsLowerHexHash(input.ExpectedConfigurationSha256))
            throw InvalidRequest();
        if (secretWriter is null)
            throw Unavailable("remote_mcp_secret_store_unavailable");

        var row = await FindOwnedConnectionAsync(issuer, actorId, connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (row.State != RemoteMcpOAuthConnectionState.NotConnected ||
            row.ConnectionRevision != input.ExpectedConnectionRevision ||
            row.CredentialRevision != input.ExpectedCredentialRevision ||
            row.ConfigurationRevision != input.ExpectedConfigurationRevision ||
            row.EnvironmentConfigurationHash != input.ExpectedConfigurationSha256)
            throw RevisionConflict();

        await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
            .ConfigureAwait(false);
        var beforeLink = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
        if (!IsExpectedPreLinkOrReplayConfiguration(row, beforeLink))
            throw RevisionConflict();

        var reference = row.IdentityBindingReference;
        if (!Guid.TryParseExact(reference, "N", out var operationId) || operationId == Guid.Empty)
            throw new InvalidOperationException("The stored Identity binding reference is invalid.");

        RemoteMcpIdentityBindingReceipt receipt;
        try
        {
            using var bindingClient = new RemoteMcpOAuthIdentityBindingClient(
                _environmentOwnerAddress, environment);
            receipt = await bindingClient.LinkAsync(
                actor,
                row.ProjectId,
                row.ConnectionId,
                new LinkRemoteMcpIdentityBindingRequest(
                    row.ConfigurationRevision,
                    row.EnvironmentConfigurationHash,
                    reference,
                    operationId.ToString("N")),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteMcpOAuthEnvironmentException error)
        {
            throw new RemoteMcpOAuthManagementException(error.Code, error.StatusCode);
        }

        await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
            .ConfigureAwait(false);
        var linkedConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
        if (!MatchesReceipt(row, linkedConfiguration, receipt))
            throw RevisionConflict();

        var binding = CreateLinkedBinding(row, receipt);
        var providerOptions = RequireBoundProvider(binding);

        var metadata = await DiscoverMetadataAsync(
            binding, providerOptions, cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        var material = RemoteMcpOAuthConsentMaterial.Create(
            binding, correlationId, time.GetUtcNow() + ConsentLifetime, time);
        var authorizationUri = RemoteMcpOAuthProtocol.BuildAuthorizationRequestUri(
            binding, metadata, binding.ClientId, material);
        var state = material.State.GetValue();
        var verifierSecretId = $"remote-mcp-{row.ConnectionId:N}-verifier";
        try
        {
            var verifierReference = await secretWriter.WriteVersionAsync(
                verifierSecretId, material.PkceVerifier, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(verifierReference.Id, verifierSecretId, StringComparison.Ordinal))
                throw Unavailable("remote_mcp_secret_store_unavailable");

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var confirmedConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!MatchesReceipt(row, confirmedConfiguration, receipt))
                throw RevisionConflict();

            var current = new RemoteMcpOAuthConnection(
                binding, row.ConnectionRevision, row.CredentialRevision, row.State);
            var transition = RemoteMcpOAuthLifecycle.TryBeginConsent(
                current, binding, row.ConnectionRevision, material, verifierReference)
                ?? throw RevisionConflict();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var updated = await db.RemoteMcpOAuthConnections
                .Where(item =>
                    item.Id == row.Id &&
                    item.OwnerId == row.OwnerId &&
                    item.OwnerIssuer == issuer &&
                    item.OwnerActorId == actorId &&
                    item.TenantId == row.TenantId &&
                    item.ProjectId == row.ProjectId &&
                    item.ConnectionId == connectionId &&
                    item.ConfigurationRevision == row.ConfigurationRevision &&
                    item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                    item.IdentityBindingReference == reference &&
                    item.BindingHash == row.BindingHash &&
                    item.ConnectionRevision == row.ConnectionRevision &&
                    item.CredentialRevision == row.CredentialRevision &&
                    item.State == RemoteMcpOAuthConnectionState.NotConnected)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ConfigurationRevision, receipt.FinalConfigurationRevision)
                    .SetProperty(item => item.EnvironmentConfigurationHash, receipt.FinalConfigurationSha256)
                    .SetProperty(item => item.BindingHash, binding.BindingHash)
                    .SetProperty(item => item.ConnectionRevision, transition.Connection.Revision)
                    .SetProperty(item => item.CredentialRevision, transition.Connection.CredentialRevision)
                    .SetProperty(item => item.State, RemoteMcpOAuthConnectionState.PendingConsent)
                    .SetProperty(item => item.UpdatedAt, time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            if (updated != 1)
                throw RevisionConflict();

            db.RemoteMcpOAuthConsents.Add(new RemoteMcpOAuthConsentRecord
            {
                Id = Guid.NewGuid(),
                ConnectionRecordId = row.Id,
                CorrelationId = transition.Consent.CorrelationId,
                BindingHash = transition.Consent.BindingHash,
                ConnectionRevision = transition.Consent.ConnectionRevision,
                StateHash = transition.Consent.StateHash,
                PkceChallenge = transition.Consent.PkceChallenge,
                VerifierSecretId = verifierReference.Id,
                VerifierSecretVersion = verifierReference.Version,
                AuthorizationEndpointUri = metadata.AuthorizationEndpoint.AbsoluteUri,
                TokenEndpointUri = metadata.TokenEndpoint.AbsoluteUri,
                Revision = transition.Consent.Revision,
                State = transition.Consent.State,
                CreatedAt = transition.Consent.CreatedAt,
                ExpiresAt = transition.Consent.ExpiresAt
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var finalConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!MatchesReceipt(row, finalConfiguration, receipt))
                throw RevisionConflict();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new RemoteMcpOAuthConsentPreparation(
                transition.Consent.CorrelationId,
                state,
                transition.Consent.PkceChallenge,
                authorizationUri,
                transition.Consent.ExpiresAt,
                transition.Connection.Revision,
                receipt.FinalConfigurationRevision,
                receipt.FinalConfigurationSha256);
        }
        finally
        {
            material.State.Invalidate();
            material.PkceVerifier.Invalidate();
        }
    }

    public async Task<RemoteMcpOAuthManagementStatus> CompleteCallbackAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        RemoteMcpOAuthCallbackRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsCallbackState(input.State) ||
            (input.Code is null) == (input.Error is null) ||
            input.Code is { Length: 0 or > 4096 } ||
            input.Code is { } code && code.Any(char.IsControl) ||
            input.Error is { Length: 0 or > 128 } ||
            input.Error is { } error && error.Any(char.IsControl) ||
            input.Error is not null && input.Error != "access_denied")
            throw InvalidRequest();
        if (secretWriter is null || _secretRedemption is null)
            throw Unavailable("remote_mcp_secret_store_unavailable");

        var stateHash = Hash(input.State);
        var (row, consentRecord) = await FindOwnedConnectionByCallbackStateAsync(
            issuer, actorId, stateHash, cancellationToken).ConfigureAwait(false);
        if (row.State != RemoteMcpOAuthConnectionState.PendingConsent)
            throw RevisionConflict();
        var binding = CreateBinding(
            row, row.ConfigurationRevision, row.EnvironmentConfigurationHash, row.IdentityBindingReference);
        var consent = ToLifecycleConsent(consentRecord);
        var connection = ToLifecycleConnection(row, binding);
        if (consentRecord.BindingHash != binding.BindingHash ||
            consentRecord.ConnectionRevision != row.ConnectionRevision)
            throw RevisionConflict();

        var providerOptions = RequireBoundProvider(binding);

        await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
            .ConfigureAwait(false);
        var currentConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
        if (!IsCurrentConfiguration(row, currentConfiguration))
            throw RevisionConflict();

        var metadata = await DiscoverMetadataAsync(binding, providerOptions, cancellationToken)
            .ConfigureAwait(false);
        if (metadata.AuthorizationEndpoint.AbsoluteUri != consentRecord.AuthorizationEndpointUri ||
            metadata.TokenEndpoint.AbsoluteUri != consentRecord.TokenEndpointUri)
            throw RevisionConflict();

        var attemptId = Guid.NewGuid();
        var now = time.GetUtcNow();
        var claimedConsent = RemoteMcpOAuthLifecycle.TryClaimCallback(
            consent, connection, binding, input.State, attemptId, consent.Revision, now)
            ?? throw RevisionConflict();
        await ClaimCallbackAsync(row, consentRecord, claimedConsent, attemptId, now, cancellationToken)
            .ConfigureAwait(false);

        if (input.Error is not null)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                RemoteMcpOAuthRequestFailure.ProviderRejected, cancellationToken).ConfigureAwait(false);
            throw new RemoteMcpOAuthManagementException(
                "remote_mcp_oauth_consent_denied", StatusCodes.Status400BadRequest);
        }

        var requestMayHaveBeenSent = false;
        SecretCredential? verifier = null;
        SecretCredential? accessToken = null;
        SecretCredential? refreshToken = null;
        try
        {
            verifier = await _secretRedemption.RedeemAsync(
                new SecretRedemptionRequest(
                    consent.ProtectedVerifierReference,
                    "remote-mcp-oauth-verifier",
                    consent.CorrelationId.ToString("N")),
                cancellationToken).ConfigureAwait(false);

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var beforeExchange = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, beforeExchange))
                throw RevisionConflict();

            var tokens = await ExchangeCodeAsync(
                consentRecord.TokenEndpointUri,
                binding,
                input.Code!,
                verifier,
                () => requestMayHaveBeenSent = true,
                cancellationToken).ConfigureAwait(false);
            accessToken = new SecretCredential(tokens.AccessToken, tokens.AccessTokenExpiresAt, time);
            if (tokens.RefreshToken is not null)
                refreshToken = new SecretCredential(
                    tokens.RefreshToken, tokens.RefreshTokenExpiresAt ?? DateTimeOffset.MaxValue, time);

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var afterExchange = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, afterExchange))
                throw RevisionConflict();

            var accessSecretId = $"remote-mcp-{row.ConnectionId:N}-access";
            var accessReference = await secretWriter.WriteVersionAsync(
                accessSecretId, accessToken, cancellationToken).ConfigureAwait(false);
            if (accessReference.Id != accessSecretId)
                throw Unavailable("remote_mcp_secret_store_unavailable");
            SecretRef? refreshReference = null;
            if (refreshToken is not null)
            {
                var refreshSecretId = $"remote-mcp-{row.ConnectionId:N}-refresh";
                refreshReference = await secretWriter.WriteVersionAsync(
                    refreshSecretId, refreshToken, cancellationToken).ConfigureAwait(false);
                if (refreshReference.Id != refreshSecretId)
                    throw Unavailable("remote_mcp_secret_store_unavailable");
            }

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var beforeCommit = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, beforeCommit))
                throw RevisionConflict();

            var completed = RemoteMcpOAuthLifecycle.TryCompleteCallback(
                claimedConsent, connection, binding, attemptId, claimedConsent.Revision,
                accessReference, refreshReference, tokens.AccessTokenExpiresAt, time.GetUtcNow(),
                tokens.RefreshTokenExpiresAt)
                ?? throw RevisionConflict();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var completedConsent = await db.RemoteMcpOAuthConsents
                .Where(item =>
                    item.Id == consentRecord.Id &&
                    item.ConnectionRecordId == row.Id &&
                    item.BindingHash == binding.BindingHash &&
                    item.ConnectionRevision == row.ConnectionRevision &&
                    item.State == RemoteMcpOAuthConsentState.CallbackClaimed &&
                    item.ClaimAttemptId == attemptId &&
                    item.Revision == claimedConsent.Revision &&
                    item.ExpiresAt > time.GetUtcNow())
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, completed.Consent.State)
                    .SetProperty(item => item.ClaimAttemptId, (Guid?)null)
                    .SetProperty(item => item.Revision, item => item.Revision + 1)
                    .SetProperty(item => item.CompletedAt, time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            var completedConnection = await db.RemoteMcpOAuthConnections
                .Where(item =>
                    item.Id == row.Id &&
                    item.OwnerId == row.OwnerId &&
                    item.OwnerIssuer == issuer &&
                    item.OwnerActorId == actorId &&
                    item.TenantId == row.TenantId &&
                    item.ProjectId == row.ProjectId &&
                    item.ConnectionId == row.ConnectionId &&
                    item.ConfigurationRevision == row.ConfigurationRevision &&
                    item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                    item.IdentityBindingReference == row.IdentityBindingReference &&
                    item.BindingHash == binding.BindingHash &&
                    item.ConnectionRevision == row.ConnectionRevision &&
                    item.CredentialRevision == row.CredentialRevision &&
                    item.State == RemoteMcpOAuthConnectionState.PendingConsent)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, completed.Connection.State)
                    .SetProperty(item => item.ConnectionRevision, completed.Connection.Revision)
                    .SetProperty(item => item.CredentialRevision, completed.Connection.CredentialRevision)
                    .SetProperty(item => item.AccessTokenSecretId, accessReference.Id)
                    .SetProperty(item => item.AccessTokenSecretVersion, accessReference.Version)
                    .SetProperty(item => item.AccessTokenExpiresAt, tokens.AccessTokenExpiresAt)
                    .SetProperty(item => item.RefreshTokenSecretId, refreshReference == null ? null : refreshReference.Id)
                    .SetProperty(item => item.RefreshTokenSecretVersion, refreshReference == null ? null : refreshReference.Version)
                    .SetProperty(item => item.RefreshTokenExpiresAt, tokens.RefreshTokenExpiresAt)
                    .SetProperty(item => item.UpdatedAt, time.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            if (completedConsent != 1 || completedConnection != 1)
                throw RevisionConflict();

            await RequireWriteProjectsAsync(actor, issuer, actorId, row, cancellationToken)
                .ConfigureAwait(false);
            var finalConfiguration = await ReadCurrentConfigurationAsync(actor, row, cancellationToken)
                .ConfigureAwait(false)
                ?? throw Unavailable("remote_mcp_environment_configuration_unavailable");
            if (!IsCurrentConfiguration(row, finalConfiguration))
                throw RevisionConflict();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var latest = await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.Id == row.Id, cancellationToken).ConfigureAwait(false);
            return ToStatus(latest, finalConfiguration);
        }
        catch (RemoteMcpOAuthTokenExchangeException exchangeError)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                exchangeError.Failure, CancellationToken.None).ConfigureAwait(false);
            throw new RemoteMcpOAuthManagementException(
                exchangeError.Failure == RemoteMcpOAuthRequestFailure.ProviderRejected
                    ? "remote_mcp_oauth_code_rejected"
                    : "remote_mcp_oauth_exchange_uncertain",
                exchangeError.Failure == RemoteMcpOAuthRequestFailure.ProviderRejected
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status503ServiceUnavailable);
        }
        catch (RemoteMcpOAuthManagementException)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (RuntimeAuthorizationException)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw Unavailable("remote_mcp_oauth_provider_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw Unavailable(requestMayHaveBeenSent
                ? "remote_mcp_oauth_exchange_uncertain"
                : "remote_mcp_oauth_provider_unavailable");
        }
        catch (AzureKeyVaultSecretException)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw Unavailable("remote_mcp_secret_store_unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResolveCallbackFailureAsync(
                row, binding, consentRecord, consent, connection, claimedConsent, attemptId,
                requestMayHaveBeenSent
                    ? RemoteMcpOAuthRequestFailure.PossiblySent
                    : RemoteMcpOAuthRequestFailure.DefinitelyNotSent,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            verifier?.Invalidate();
            accessToken?.Invalidate();
            refreshToken?.Invalidate();
        }
    }

    private RemoteMcpOAuthProviderOptions RequireBoundProvider(
        RemoteMcpOAuthConnectionBinding binding)
    {
        var provider = options.Providers.SingleOrDefault(item =>
            string.Equals(item.IssuerUri, binding.Issuer.AbsoluteUri, StringComparison.Ordinal))
            ?? throw Denied("remote_mcp_oauth_provider_unapproved");
        if (!provider.ApprovedResources.Contains(binding.Resource.AbsoluteUri, StringComparer.Ordinal))
            throw Denied("remote_mcp_oauth_resource_unapproved");
        if (!string.Equals(binding.ClientId, provider.ClientId, StringComparison.Ordinal) ||
            !string.Equals(binding.RedirectUri.AbsoluteUri, provider.RedirectUri, StringComparison.Ordinal))
            throw Denied("remote_mcp_oauth_provider_unapproved");
        if (binding.Scopes.Any(scope => !provider.ApprovedScopes.Contains(scope, StringComparer.Ordinal)))
            throw Denied("remote_mcp_oauth_scope_unapproved");
        return provider;
    }

    private async Task<RemoteMcpOAuthServerMetadata> DiscoverMetadataAsync(
        RemoteMcpOAuthConnectionBinding binding,
        RemoteMcpOAuthProviderOptions providerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            var resourceMetadata = await ReadMetadataAsync(
                RemoteMcpOAuthProtocol.GetProtectedResourceMetadataUri(binding.Resource),
                cancellationToken).ConfigureAwait(false);
            var authorizationServerMetadata = await ReadMetadataAsync(
                RemoteMcpOAuthProtocol.GetAuthorizationServerMetadataUri(binding.Issuer),
                cancellationToken).ConfigureAwait(false);
            return RemoteMcpOAuthProtocol.ValidateMetadata(
                binding,
                resourceMetadata,
                authorizationServerMetadata,
                providerOptions.ApprovedOAuthEndpoints.Select(endpoint => new Uri(endpoint)));
        }
        catch (RemoteMcpOAuthProtocolException error)
        {
            throw new RemoteMcpOAuthManagementException(error.Code, StatusCodes.Status409Conflict);
        }
        catch (HttpRequestException)
        {
            throw Unavailable("remote_mcp_oauth_metadata_unavailable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("remote_mcp_oauth_metadata_unavailable");
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadMetadataAsync(
        Uri address,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataRequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        using var response = await provider.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri != address ||
            !response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is > MaximumMetadataBytes)
            throw new HttpRequestException("Remote MCP OAuth metadata response is invalid.");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > MaximumMetadataBytes)
                throw new HttpRequestException("Remote MCP OAuth metadata response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<RemoteMcpOAuthTokenSet> ExchangeCodeAsync(
        string tokenEndpoint,
        RemoteMcpOAuthConnectionBinding binding,
        string code,
        SecretCredential verifier,
        Action requestWillBeSent,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProviderRequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = binding.ClientId,
            ["redirect_uri"] = binding.RedirectUri.AbsoluteUri,
            ["code_verifier"] = verifier.GetValue(),
            ["resource"] = binding.Resource.AbsoluteUri
        });

        requestWillBeSent();
        using var response = await provider.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri?.AbsoluteUri != tokenEndpoint)
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            var failure = await IsInvalidGrantResponseAsync(response.Content, timeout.Token)
                .ConfigureAwait(false)
                ? RemoteMcpOAuthRequestFailure.ProviderRejected
                : RemoteMcpOAuthRequestFailure.PossiblySent;
            throw new RemoteMcpOAuthTokenExchangeException(failure);
        }
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);

        byte[] body;
        try
        {
            body = await ReadBoundedContentAsync(
                response.Content, MaximumMetadataBytes, timeout.Token).ConfigureAwait(false);
            return ParseTokenResponse(body, binding.Scopes, time.GetUtcNow());
        }
        catch (JsonException)
        {
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);
        }
        catch (InvalidDataException)
        {
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);
        }
    }

    private static async Task<bool> IsInvalidGrantResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentType?.MediaType != "application/json")
            return false;
        try
        {
            var body = await ReadBoundedContentAsync(
                content, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetSingleProperty(document.RootElement, "error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                error.GetString() == "invalid_grant";
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task<RemoteMcpOAuthTokenSet> ExchangeRefreshTokenAsync(
        string tokenEndpoint,
        RemoteMcpOAuthConnectionBinding binding,
        SecretCredential refreshToken,
        Action requestWillBeSent,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProviderRequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken.GetValue(),
            ["client_id"] = binding.ClientId,
            ["resource"] = binding.Resource.AbsoluteUri
        });

        requestWillBeSent();
        using var response = await provider.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri?.AbsoluteUri != tokenEndpoint)
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            var failure = await IsInvalidGrantResponseAsync(response.Content, timeout.Token)
                .ConfigureAwait(false)
                ? RemoteMcpOAuthRequestFailure.ProviderRejected
                : RemoteMcpOAuthRequestFailure.PossiblySent;
            throw new RemoteMcpOAuthTokenExchangeException(failure);
        }
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);

        try
        {
            var body = await ReadBoundedContentAsync(
                response.Content, MaximumMetadataBytes, timeout.Token).ConfigureAwait(false);
            return ParseTokenResponse(body, binding.Scopes, time.GetUtcNow());
        }
        catch (JsonException)
        {
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);
        }
        catch (InvalidDataException)
        {
            throw new RemoteMcpOAuthTokenExchangeException(RemoteMcpOAuthRequestFailure.PossiblySent);
        }
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength.Value > maximumBytes)
            throw new InvalidDataException("OAuth response exceeds the maximum size.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("OAuth response exceeds the maximum size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static RemoteMcpOAuthTokenSet ParseTokenResponse(
        ReadOnlyMemory<byte> bytes,
        IReadOnlyList<string> requestedScopes,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException();

        var accessToken = ReadTokenString(root, "access_token", required: true)
            ?? throw new JsonException();
        var tokenType = ReadTokenString(root, "token_type", required: true);
        if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new JsonException();
        var expiresIn = ReadExpirySeconds(root, "expires_in", required: true);
        var refreshToken = ReadTokenString(root, "refresh_token", required: false);
        var refreshExpiresIn = ReadExpirySeconds(root, "refresh_token_expires_in", required: false);
        if (refreshExpiresIn is not null && refreshToken is null)
            throw new JsonException();

        var scope = ReadTokenString(root, "scope", required: false);
        if (scope is not null)
        {
            var returnedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (returnedScopes.Distinct(StringComparer.Ordinal).Count() != returnedScopes.Length ||
                !returnedScopes.Order(StringComparer.Ordinal)
                    .SequenceEqual(requestedScopes.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new JsonException();
        }

        return new RemoteMcpOAuthTokenSet(
            accessToken,
            refreshToken,
            now.AddSeconds(expiresIn!.Value),
            refreshExpiresIn is null ? null : now.AddSeconds(refreshExpiresIn.Value));
    }

    private static string? ReadTokenString(JsonElement root, string name, bool required)
    {
        if (!TryGetSingleProperty(root, name, out var value))
        {
            if (required)
                throw new JsonException();
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException();
        var result = value.GetString();
        if (string.IsNullOrEmpty(result) || result.Length > 16_384 ||
            result.Any(char.IsControl))
            throw new JsonException();
        return result;
    }

    private static long? ReadExpirySeconds(JsonElement root, string name, bool required)
    {
        if (!TryGetSingleProperty(root, name, out var value))
        {
            if (required)
                throw new JsonException();
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var seconds) ||
            seconds is < 1 or > 31_536_000)
            throw new JsonException();
        return seconds;
    }

    private static bool TryGetSingleProperty(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals(name))
                continue;
            value = property.Value;
            count++;
        }
        if (count > 1)
            throw new JsonException();
        return count == 1;
    }

    private async Task ClaimRefreshAsync(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthConnection current,
        RemoteMcpOAuthConnection claimed,
        RemoteMcpOAuthRefreshClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var updated = await db.RemoteMcpOAuthConnections
            .Where(item =>
                item.Id == row.Id &&
                item.OwnerId == row.OwnerId &&
                item.OwnerIssuer == row.OwnerIssuer &&
                item.OwnerActorId == row.OwnerActorId &&
                item.TenantId == row.TenantId &&
                item.ProjectId == row.ProjectId &&
                item.ConnectionId == row.ConnectionId &&
                item.ConfigurationRevision == row.ConfigurationRevision &&
                item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                item.IdentityBindingReference == row.IdentityBindingReference &&
                item.BindingHash == current.Binding.BindingHash &&
                item.ConnectionRevision == current.Revision &&
                item.CredentialRevision == current.CredentialRevision &&
                item.State == RemoteMcpOAuthConnectionState.Authorized &&
                item.AccessTokenSecretId == current.AccessTokenReference!.Id &&
                item.AccessTokenSecretVersion == current.AccessTokenReference.Version &&
                item.RefreshTokenSecretId == claim.RefreshTokenReference.Id &&
                item.RefreshTokenSecretVersion == claim.RefreshTokenReference.Version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, claimed.State)
                .SetProperty(item => item.ConnectionRevision, claimed.Revision)
                .SetProperty(item => item.CredentialRevision, claimed.CredentialRevision)
                .SetProperty(item => item.RefreshAttemptId, claimed.RefreshAttemptId)
                .SetProperty(item => item.RefreshStartedAt, claimed.RefreshStartedAt)
                .SetProperty(item => item.UpdatedAt, now),
                cancellationToken).ConfigureAwait(false);
        if (updated != 1)
            throw RevisionConflict();
    }

    private async Task ResolveRefreshFailureAsync(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthConnectionBinding binding,
        RemoteMcpOAuthConnection claimed,
        RemoteMcpOAuthRefreshClaim claim,
        RemoteMcpOAuthRequestFailure failure,
        CancellationToken cancellationToken)
    {
        var resolved = RemoteMcpOAuthLifecycle.TryResolveRefreshFailure(
            claimed, binding, claim, claim.ConnectionRevision, failure)
            ?? throw RevisionConflict();
        var updated = await db.RemoteMcpOAuthConnections
            .Where(item =>
                item.Id == row.Id &&
                item.OwnerId == row.OwnerId &&
                item.OwnerIssuer == row.OwnerIssuer &&
                item.OwnerActorId == row.OwnerActorId &&
                item.TenantId == row.TenantId &&
                item.ProjectId == row.ProjectId &&
                item.ConnectionId == row.ConnectionId &&
                item.ConfigurationRevision == row.ConfigurationRevision &&
                item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                item.IdentityBindingReference == row.IdentityBindingReference &&
                item.BindingHash == claim.BindingHash &&
                item.ConnectionRevision == claim.ConnectionRevision &&
                item.CredentialRevision == claim.CredentialRevision &&
                item.State == RemoteMcpOAuthConnectionState.RefreshInProgress &&
                item.RefreshAttemptId == claim.AttemptId &&
                item.RefreshTokenSecretId == claim.RefreshTokenReference.Id &&
                item.RefreshTokenSecretVersion == claim.RefreshTokenReference.Version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, resolved.State)
                .SetProperty(item => item.ConnectionRevision, resolved.Revision)
                .SetProperty(item => item.CredentialRevision, resolved.CredentialRevision)
                .SetProperty(item => item.AccessTokenSecretId,
                    resolved.AccessTokenReference == null ? null : resolved.AccessTokenReference.Id)
                .SetProperty(item => item.AccessTokenSecretVersion,
                    resolved.AccessTokenReference == null ? null : resolved.AccessTokenReference.Version)
                .SetProperty(item => item.AccessTokenExpiresAt, resolved.AccessTokenExpiresAt)
                .SetProperty(item => item.RefreshTokenSecretId,
                    resolved.RefreshTokenReference == null ? null : resolved.RefreshTokenReference.Id)
                .SetProperty(item => item.RefreshTokenSecretVersion,
                    resolved.RefreshTokenReference == null ? null : resolved.RefreshTokenReference.Version)
                .SetProperty(item => item.RefreshTokenExpiresAt, resolved.RefreshTokenExpiresAt)
                .SetProperty(item => item.RefreshAttemptId, resolved.RefreshAttemptId)
                .SetProperty(item => item.RefreshStartedAt, resolved.RefreshStartedAt)
                .SetProperty(item => item.UpdatedAt, time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        if (updated == 1)
            return;

        var latest = await db.RemoteMcpOAuthConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.Id, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound();
        if (latest.State != RemoteMcpOAuthConnectionState.RefreshInProgress ||
            latest.RefreshAttemptId != claim.AttemptId)
            return;
        throw RevisionConflict();
    }

    private async Task<RemoteMcpOAuthConnectionRecord> RecoverStaleRefreshAsync(
        RemoteMcpOAuthConnectionRecord row,
        CancellationToken cancellationToken)
    {
        var binding = CreateBinding(
            row, row.ConfigurationRevision, row.EnvironmentConfigurationHash, row.IdentityBindingReference);
        var current = ToLifecycleConnection(row, binding);
        var attemptId = row.RefreshAttemptId ?? throw RevisionConflict();
        var refreshReference = current.RefreshTokenReference ?? throw RevisionConflict();
        var claim = new RemoteMcpOAuthRefreshClaim(
            row.ConnectionId, binding.BindingHash, row.ConnectionRevision,
            row.CredentialRevision, attemptId, refreshReference);
        var resolved = RemoteMcpOAuthLifecycle.TryResolveRefreshFailure(
            current, binding, claim, row.ConnectionRevision,
            RemoteMcpOAuthRequestFailure.PossiblySent)
            ?? throw RevisionConflict();
        var updated = await db.RemoteMcpOAuthConnections
            .Where(item =>
                item.Id == row.Id &&
                item.OwnerId == row.OwnerId &&
                item.OwnerIssuer == row.OwnerIssuer &&
                item.OwnerActorId == row.OwnerActorId &&
                item.TenantId == row.TenantId &&
                item.ProjectId == row.ProjectId &&
                item.ConnectionId == row.ConnectionId &&
                item.ConfigurationRevision == row.ConfigurationRevision &&
                item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                item.IdentityBindingReference == row.IdentityBindingReference &&
                item.BindingHash == binding.BindingHash &&
                item.ConnectionRevision == row.ConnectionRevision &&
                item.CredentialRevision == row.CredentialRevision &&
                item.State == RemoteMcpOAuthConnectionState.RefreshInProgress &&
                item.RefreshAttemptId == attemptId &&
                item.RefreshStartedAt == row.RefreshStartedAt &&
                item.RefreshStartedAt <= time.GetUtcNow() - RefreshRecoveryAge)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, resolved.State)
                .SetProperty(item => item.ConnectionRevision, resolved.Revision)
                .SetProperty(item => item.CredentialRevision, resolved.CredentialRevision)
                .SetProperty(item => item.RefreshAttemptId, resolved.RefreshAttemptId)
                .SetProperty(item => item.RefreshStartedAt, resolved.RefreshStartedAt)
                .SetProperty(item => item.UpdatedAt, time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        if (updated == 1)
        {
            return await db.RemoteMcpOAuthConnections.AsNoTracking()
                .SingleAsync(item => item.Id == row.Id, cancellationToken).ConfigureAwait(false);
        }

        var latest = await db.RemoteMcpOAuthConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.Id, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound();
        if (latest.State != RemoteMcpOAuthConnectionState.RefreshInProgress ||
            latest.RefreshAttemptId != attemptId)
            return latest;
        throw RevisionConflict();
    }

    private async Task ClaimCallbackAsync(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthConsentRecord consent,
        RemoteMcpOAuthPendingConsent claimedConsent,
        Guid attemptId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var claimed = await db.RemoteMcpOAuthConsents
            .Where(item =>
                item.Id == consent.Id &&
                item.ConnectionRecordId == row.Id &&
                item.BindingHash == row.BindingHash &&
                item.ConnectionRevision == row.ConnectionRevision &&
                item.State == RemoteMcpOAuthConsentState.Pending &&
                item.Revision == consent.Revision &&
                item.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, RemoteMcpOAuthConsentState.CallbackClaimed)
                .SetProperty(item => item.ClaimAttemptId, attemptId)
                .SetProperty(item => item.Revision, claimedConsent.Revision),
                cancellationToken).ConfigureAwait(false);
        var current = await db.RemoteMcpOAuthConnections
            .Where(item =>
                item.Id == row.Id &&
                item.OwnerId == row.OwnerId &&
                item.OwnerIssuer == row.OwnerIssuer &&
                item.OwnerActorId == row.OwnerActorId &&
                item.TenantId == row.TenantId &&
                item.ProjectId == row.ProjectId &&
                item.ConnectionId == row.ConnectionId &&
                item.BindingHash == row.BindingHash &&
                item.ConnectionRevision == row.ConnectionRevision &&
                item.CredentialRevision == row.CredentialRevision &&
                item.State == RemoteMcpOAuthConnectionState.PendingConsent)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.UpdatedAt, now),
                cancellationToken).ConfigureAwait(false);
        if (claimed != 1 || current != 1)
            throw RevisionConflict();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveCallbackFailureAsync(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthConnectionBinding binding,
        RemoteMcpOAuthConsentRecord consentRecord,
        RemoteMcpOAuthPendingConsent consent,
        RemoteMcpOAuthConnection connection,
        RemoteMcpOAuthPendingConsent claimedConsent,
        Guid attemptId,
        RemoteMcpOAuthRequestFailure failure,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var resolvedConsent = RemoteMcpOAuthLifecycle.TryResolveCallbackFailure(
            claimedConsent, connection, binding, attemptId, claimedConsent.Revision, failure, now)
            ?? throw RevisionConflict();
        var resolvedConnection = RemoteMcpOAuthLifecycle.TryCloseFailedConsent(
            resolvedConsent, connection, binding);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var consentUpdated = await db.RemoteMcpOAuthConsents
            .Where(item =>
                item.Id == consentRecord.Id &&
                item.ConnectionRecordId == row.Id &&
                item.BindingHash == binding.BindingHash &&
                item.ConnectionRevision == row.ConnectionRevision &&
                item.State == RemoteMcpOAuthConsentState.CallbackClaimed &&
                item.ClaimAttemptId == attemptId &&
                item.Revision == claimedConsent.Revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, resolvedConsent.State)
                .SetProperty(item => item.ClaimAttemptId, (Guid?)null)
                .SetProperty(item => item.Revision, item => item.Revision + 1)
                .SetProperty(
                    item => item.CompletedAt,
                    resolvedConsent.State == RemoteMcpOAuthConsentState.Pending ? null : now),
                cancellationToken).ConfigureAwait(false);
        if (consentUpdated != 1)
            throw RevisionConflict();

        if (resolvedConnection is not null)
        {
            var connectionUpdated = await db.RemoteMcpOAuthConnections
                .Where(item =>
                    item.Id == row.Id &&
                    item.OwnerId == row.OwnerId &&
                    item.OwnerIssuer == row.OwnerIssuer &&
                    item.OwnerActorId == row.OwnerActorId &&
                    item.TenantId == row.TenantId &&
                    item.ProjectId == row.ProjectId &&
                    item.ConnectionId == row.ConnectionId &&
                    item.ConfigurationRevision == row.ConfigurationRevision &&
                    item.EnvironmentConfigurationHash == row.EnvironmentConfigurationHash &&
                    item.IdentityBindingReference == row.IdentityBindingReference &&
                    item.BindingHash == binding.BindingHash &&
                    item.ConnectionRevision == connection.Revision &&
                    item.CredentialRevision == connection.CredentialRevision &&
                    item.State == RemoteMcpOAuthConnectionState.PendingConsent)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, resolvedConnection.State)
                    .SetProperty(item => item.ConnectionRevision, resolvedConnection.Revision)
                    .SetProperty(item => item.CredentialRevision, resolvedConnection.CredentialRevision)
                    .SetProperty(item => item.AccessTokenSecretId, (string?)null)
                    .SetProperty(item => item.AccessTokenSecretVersion, (string?)null)
                    .SetProperty(item => item.AccessTokenExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.RefreshTokenSecretId, (string?)null)
                    .SetProperty(item => item.RefreshTokenSecretVersion, (string?)null)
                    .SetProperty(item => item.RefreshTokenExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.RefreshAttemptId, (Guid?)null)
                    .SetProperty(item => item.RefreshStartedAt, (DateTimeOffset?)null)
                    .SetProperty(item => item.UpdatedAt, now),
                    cancellationToken).ConfigureAwait(false);
            if (connectionUpdated != 1)
                throw RevisionConflict();
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteMcpOAuthConnectionRecord> FindOwnedConnectionAsync(
        string issuer,
        string actorId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(actorId) ||
            connectionId == Guid.Empty)
            throw NotFound();

        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            user => user.Issuer == issuer && user.Subject == actorId && !user.Disabled,
            cancellationToken).ConfigureAwait(false);
        if (owner is null)
            throw NotFound();

        return await db.RemoteMcpOAuthConnections.AsNoTracking().SingleOrDefaultAsync(row =>
                row.OwnerId == owner.Id && row.OwnerIssuer == issuer &&
                row.OwnerActorId == actorId && row.ConnectionId == connectionId,
                cancellationToken).ConfigureAwait(false)
            ?? throw NotFound();
    }

    private async Task<(RemoteMcpOAuthConnectionRecord Connection, RemoteMcpOAuthConsentRecord Consent)>
        FindOwnedConnectionByCallbackStateAsync(
            string issuer,
            string actorId,
            string stateHash,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(actorId) ||
            !IsLowerHexHash(stateHash))
            throw NotFound();

        var match = await (
                from owner in db.Users.AsNoTracking()
                join connection in db.RemoteMcpOAuthConnections.AsNoTracking()
                    on owner.Id equals connection.OwnerId
                join consent in db.RemoteMcpOAuthConsents.AsNoTracking()
                    on connection.Id equals consent.ConnectionRecordId
                where owner.Issuer == issuer && owner.Subject == actorId && !owner.Disabled &&
                    connection.OwnerIssuer == issuer && connection.OwnerActorId == actorId &&
                    consent.StateHash == stateHash
                select new { Connection = connection, Consent = consent })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return match is null
            ? throw NotFound()
            : (match.Connection, match.Consent);
    }

    private async Task RequireReadProjectsAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        RemoteMcpOAuthConnectionRecord row,
        CancellationToken cancellationToken) =>
        await RequireProjectPermissionAsync(actor, issuer, actorId, row,
            ProjectAuthorizationPermission.ReadProjects, cancellationToken).ConfigureAwait(false);

    private async Task RequireWriteProjectsAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        RemoteMcpOAuthConnectionRecord row,
        CancellationToken cancellationToken) =>
        await RequireProjectPermissionAsync(actor, issuer, actorId, row,
            ProjectAuthorizationPermission.WriteProjects, cancellationToken).ConfigureAwait(false);

    private async Task RequireProjectPermissionAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        RemoteMcpOAuthConnectionRecord row,
        ProjectAuthorizationPermission requiredPermission,
        CancellationToken cancellationToken) =>
        _ = await RequireProjectPermissionAsync(
            actor, issuer, actorId, row.ProjectId, row.TenantId, requiredPermission, cancellationToken)
            .ConfigureAwait(false);

    private async Task<ProjectAuthorizationContextResponse> RequireProjectPermissionAsync(
        RuntimeActorAuthorization actor,
        string issuer,
        string actorId,
        string projectId,
        string? expectedTenantId,
        ProjectAuthorizationPermission requiredPermission,
        CancellationToken cancellationToken)
    {
        if (!actor.Bearer.IsUsable() ||
            actor.TenantSelector is not null &&
                expectedTenantId is not null && actor.TenantSelector != expectedTenantId)
            throw Denied("remote_mcp_owner_denied");

        var context = await RuntimeOwnerHttpTransport.SendAsync<ProjectAuthorizationContextResponse>(
            projects, _projectsOwnerAddress, "/api/authorization/context", actor, null, cancellationToken)
            .ConfigureAwait(false);
        if (context.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            context.Issuer != issuer || context.ActorId != actorId ||
            context.MembershipRevision <= 0 || context.EffectiveAuthority.IsDefault ||
            context.BoundProjectId is not null || context.BoundRunId is not null ||
            expectedTenantId is not null && context.TenantId != expectedTenantId ||
            actor.TenantSelector is not null && context.TenantId != actor.TenantSelector ||
            !context.EffectiveAuthority.Any(resource =>
                resource.Permissions.Any(permission =>
                    permission.Permission == requiredPermission &&
                    permission.RoleRevision > 0 &&
                    (resource.ResourceType == ProjectAuthorityResourceType.Project &&
                        resource.ResourceId == projectId ||
                     resource.ResourceType == ProjectAuthorityResourceType.Tenant &&
                        resource.ResourceId == context.TenantId))) ||
            !actor.Bearer.IsUsable())
            throw Denied("remote_mcp_owner_denied");

        var project = await RuntimeOwnerHttpTransport.SendAsync<ProjectSummaryProof>(
            projects, _projectsOwnerAddress,
            $"/api/projects/{Uri.EscapeDataString(projectId)}", actor, null, cancellationToken)
            .ConfigureAwait(false);
        if (project.ProjectId != projectId || project.Revision <= 0 || project.State != "active" ||
            !actor.Bearer.IsUsable())
            throw Denied("remote_mcp_project_unavailable");
        return context;
    }

    private async Task<CurrentConfiguration?> ReadCurrentConfigurationAsync(
        RuntimeActorAuthorization actor,
        RemoteMcpOAuthConnectionRecord row,
        CancellationToken cancellationToken) =>
        await ReadCurrentConfigurationAsync(
            actor, row.ProjectId, row.ConnectionId, cancellationToken).ConfigureAwait(false);

    private async Task<CurrentConfiguration?> ReadCurrentConfigurationAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/remote-mcp/connections/{connectionId:D}";
        var snapshot = await RuntimeOwnerHttpTransport.ReadOptionalAsync<EnvironmentSnapshot>(
            environment, _environmentOwnerAddress, path, actor, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;
        if (snapshot.Head is null || snapshot.Head.Connection is null || snapshot.Configuration is null ||
            snapshot.Configuration.Connection is null ||
            snapshot.Head.RowRevision <= 0 || snapshot.Head.CurrentConfigurationRevision <= 0 ||
        snapshot.Head.Connection.ConnectionId != connectionId ||
        snapshot.Head.Connection.ProjectId != projectId ||
        snapshot.Configuration.Connection.ConnectionId != connectionId ||
        snapshot.Configuration.Connection.ProjectId != projectId ||
            snapshot.Configuration.ConfigurationRevision != snapshot.Head.CurrentConfigurationRevision)
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");

        var configuration = await RuntimeOwnerHttpTransport.ReadOptionalAsync<EnvironmentConfiguration>(
            environment, _environmentOwnerAddress,
            $"{path}/configurations/{snapshot.Head.CurrentConfigurationRevision}",
            actor, cancellationToken).ConfigureAwait(false);
        if (configuration is null)
            return null;
        if (configuration.ConfigurationRevision != snapshot.Head.CurrentConfigurationRevision ||
            configuration.Connection is null ||
            configuration.Connection.ConnectionId != connectionId ||
            configuration.Connection.ProjectId != projectId ||
            !SameConfiguration(snapshot.Configuration, configuration) ||
            !IsLowerHexHash(configuration.ConfigurationSha256) ||
            !IsCanonicalHttpsUri(configuration.EndpointUri) ||
            configuration.ResourceUri is { } resourceUri && !IsCanonicalHttpsUri(resourceUri) ||
            string.IsNullOrWhiteSpace(configuration.AuthenticationMode) ||
            string.IsNullOrWhiteSpace(configuration.TransportProfile) ||
            string.IsNullOrWhiteSpace(snapshot.Head.State))
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");

        return new CurrentConfiguration(
            configuration.ConfigurationRevision,
            configuration.ConfigurationSha256,
            configuration.EndpointUri,
            configuration.ResourceUri,
            configuration.AuthenticationMode,
            configuration.TransportProfile,
            configuration.IdentityBindingReference)
        {
            ConnectionState = snapshot.Head.State
        };
    }

    private static bool SameConfiguration(
        EnvironmentConfiguration left,
        EnvironmentConfiguration right) =>
        left.ConfigurationRevision == right.ConfigurationRevision &&
        left.Connection?.ProjectId == right.Connection?.ProjectId &&
        left.Connection?.ConnectionId == right.Connection?.ConnectionId &&
        left.ConfigurationSha256 == right.ConfigurationSha256 &&
        left.EndpointUri == right.EndpointUri &&
        left.ResourceUri == right.ResourceUri &&
        left.AuthenticationMode == right.AuthenticationMode &&
        left.TransportProfile == right.TransportProfile &&
        left.IdentityBindingReference == right.IdentityBindingReference;

    // Environment digests and references detect configuration drift; neither proves consent or credential authority.
    internal static bool IsCurrentConfiguration(
        RemoteMcpOAuthConnectionRecord row,
        CurrentConfiguration? configuration) =>
        configuration is not null &&
        IsKnownEnvironmentConnectionState(configuration.ConnectionState) &&
        !configuration.ConnectionState.Equals("removed", StringComparison.Ordinal) &&
        configuration.Revision == row.ConfigurationRevision &&
        configuration.Hash == row.EnvironmentConfigurationHash &&
        configuration.EndpointUri == row.EndpointUri &&
        configuration.ResourceUri == row.ResourceUri &&
        configuration.IdentityBindingReference is not null &&
        configuration.IdentityBindingReference == row.IdentityBindingReference &&
        configuration.AuthenticationMode == OAuthAuthenticationMode &&
        configuration.TransportProfile == TransportProfile &&
        row.TransportProfile == RemoteMcpOAuthConnectionBinding.SupportedTransportProfile;

    internal static RemoteMcpOAuthManagementStatus ToStatus(
        RemoteMcpOAuthConnectionRecord row,
        CurrentConfiguration? configuration) =>
        new(
            row.ConnectionId,
            row.ProjectId,
            row.ConnectionRevision,
            row.CredentialRevision,
            row.State.ToString(),
            row.ConfigurationRevision,
            configuration?.Revision,
            IsCurrentConfiguration(row, configuration),
            CredentialUseAvailable: false);

    private static bool IsSameDisconnect(
        RemoteMcpOAuthConnectionRecord row,
        string idempotencyHash,
        string requestHash) =>
        row.LastDisconnectIdempotencyHash == idempotencyHash &&
        row.LastDisconnectRequestHash == requestHash &&
        row.LastDisconnectResultRevision is not null;

    private static string DisconnectRequestHash(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthDisconnectRequest input,
        CurrentConfiguration currentConfiguration) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            row.OwnerId,
            row.OwnerIssuer,
            row.OwnerActorId,
            row.TenantId,
            row.ProjectId,
            row.ConnectionId,
            input.ExpectedConnectionRevision,
            input.ExpectedCredentialRevision,
            input.ExpectedConfigurationRevision,
            currentConfiguration.Hash,
            currentConfiguration.IdentityBindingReference
        }, Json));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool IsLowerHexHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCallbackState(string? state) =>
        state is { Length: 43 } && state.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '_' or '-');

    private static bool IsCanonicalHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        value.Length <= 2048 &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsoluteUri == value;

    private static bool IsKnownEnvironmentConnectionState(string value) =>
        value is "draft" or "enabled" or "disabled" or "removed";

    private static bool IsExpectedPreLinkOrReplayConfiguration(
        RemoteMcpOAuthConnectionRecord row,
        CurrentConfiguration configuration)
    {
        var isPreLink =
            configuration.Revision == row.ConfigurationRevision &&
            configuration.Hash == row.EnvironmentConfigurationHash &&
            configuration.IdentityBindingReference is null;
        var isReplay =
            configuration.Revision > row.ConfigurationRevision &&
            configuration.IdentityBindingReference == row.IdentityBindingReference;
        return IsKnownEnvironmentConnectionState(configuration.ConnectionState) &&
            !configuration.ConnectionState.Equals("removed", StringComparison.Ordinal) &&
            configuration.EndpointUri == row.EndpointUri &&
            configuration.ResourceUri == row.ResourceUri &&
            configuration.AuthenticationMode == OAuthAuthenticationMode &&
            configuration.TransportProfile == TransportProfile &&
            row.TransportProfile == RemoteMcpOAuthConnectionBinding.SupportedTransportProfile &&
            (isPreLink || isReplay);
    }

    private static bool IsUnlinkedConfiguration(
        CurrentConfiguration? configuration,
        RemoteMcpOAuthConnectionRegistrationRequest input) =>
        configuration is not null &&
        configuration.ConnectionState == "draft" &&
        configuration.Revision == input.ExpectedConfigurationRevision &&
        configuration.Hash == input.ExpectedConfigurationSha256 &&
        configuration.ResourceUri is not null &&
        IsCanonicalHttpsUri(configuration.EndpointUri) &&
        IsCanonicalHttpsUri(configuration.ResourceUri) &&
        configuration.IdentityBindingReference is null &&
        configuration.AuthenticationMode == OAuthAuthenticationMode &&
        configuration.TransportProfile == TransportProfile;

    private static bool MatchesReceipt(
        RemoteMcpOAuthConnectionRecord row,
        CurrentConfiguration configuration,
        RemoteMcpIdentityBindingReceipt receipt) =>
        configuration.ConnectionState.Equals("draft", StringComparison.Ordinal) &&
        configuration.Revision == receipt.FinalConfigurationRevision &&
        configuration.Hash == receipt.FinalConfigurationSha256 &&
        configuration.IdentityBindingReference == receipt.IdentityBindingReference &&
        configuration.EndpointUri == row.EndpointUri &&
        configuration.ResourceUri == row.ResourceUri &&
        configuration.AuthenticationMode == OAuthAuthenticationMode &&
        configuration.TransportProfile == TransportProfile &&
        row.TransportProfile == RemoteMcpOAuthConnectionBinding.SupportedTransportProfile &&
        receipt.ProjectId == row.ProjectId &&
        receipt.ConnectionId == row.ConnectionId &&
        receipt.OperationId == row.IdentityBindingReference &&
        receipt.IdentityBindingReference == row.IdentityBindingReference;

    private static RemoteMcpOAuthConnectionBinding CreateLinkedBinding(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpIdentityBindingReceipt receipt)
    {
        return CreateBinding(
            row,
            receipt.FinalConfigurationRevision,
            receipt.FinalConfigurationSha256,
            receipt.IdentityBindingReference);
    }

    private static RemoteMcpOAuthConnectionBinding CreateBinding(
        RemoteMcpOAuthConnectionRecord row,
        long configurationRevision,
        string configurationHash,
        string identityBindingReference)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(row.ScopesJson, Json)
            ?? throw new InvalidOperationException("The stored OAuth scope registration is invalid.");
        return new RemoteMcpOAuthConnectionBinding(
            row.OwnerId.ToString("D"),
            row.TenantId,
            row.ProjectId,
            row.ConnectionId,
            configurationRevision,
            configurationHash,
            identityBindingReference,
            new Uri(row.EndpointUri),
            new Uri(row.ResourceUri),
            new Uri(row.IssuerUri),
            row.ClientId,
            new Uri(row.RedirectUri),
            row.TransportProfile,
            scopes);
    }

    private static RemoteMcpOAuthPendingConsent ToLifecycleConsent(
        RemoteMcpOAuthConsentRecord consent) =>
        new(
            consent.CorrelationId,
            consent.BindingHash,
            consent.ConnectionRevision,
            consent.StateHash,
            consent.PkceChallenge,
            new SecretRef(consent.VerifierSecretId, consent.VerifierSecretVersion),
            consent.Revision,
            consent.State,
            consent.CreatedAt,
            consent.ExpiresAt,
            consent.ClaimAttemptId);

    private static RemoteMcpOAuthConnection ToLifecycleConnection(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpOAuthConnectionBinding binding)
    {
        var accessReference = row.AccessTokenSecretId is null
            ? null
            : new SecretRef(row.AccessTokenSecretId, row.AccessTokenSecretVersion!);
        var refreshReference = row.RefreshTokenSecretId is null
            ? null
            : new SecretRef(row.RefreshTokenSecretId, row.RefreshTokenSecretVersion!);
        return new RemoteMcpOAuthConnection(
            binding,
            row.ConnectionRevision,
            row.CredentialRevision,
            row.State,
            accessReference,
            refreshReference,
            row.AccessTokenExpiresAt,
            row.RefreshAttemptId,
            row.RefreshStartedAt,
            row.RefreshTokenExpiresAt);
    }

    private sealed record RemoteMcpOAuthTokenSet(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset AccessTokenExpiresAt,
        DateTimeOffset? RefreshTokenExpiresAt);

    private sealed class RemoteMcpOAuthTokenExchangeException(
        RemoteMcpOAuthRequestFailure failure) : Exception("remote_mcp_oauth_token_exchange_failed")
    {
        public RemoteMcpOAuthRequestFailure Failure { get; } = failure;
    }

    private static RemoteMcpOAuthManagementException InvalidRequest() =>
        new("remote_mcp_request_invalid", StatusCodes.Status400BadRequest);

    private static RemoteMcpOAuthManagementException NotFound() =>
        new("remote_mcp_connection_not_found", StatusCodes.Status404NotFound);

    private static RemoteMcpOAuthManagementException RevisionConflict() =>
        new("remote_mcp_connection_revision_conflict", StatusCodes.Status409Conflict);

    private static RemoteMcpOAuthManagementException Denied(string code) =>
        new(code, StatusCodes.Status403Forbidden);

    private static RemoteMcpOAuthManagementException Unavailable(string code) =>
        new(code, StatusCodes.Status503ServiceUnavailable);

    internal sealed record CurrentConfiguration(
        long Revision,
        string Hash,
        string EndpointUri,
        string? ResourceUri,
        string AuthenticationMode,
        string TransportProfile,
        string? IdentityBindingReference)
    {
        internal string ConnectionState { get; init; } = string.Empty;
    }

    private sealed record EnvironmentSnapshot(
        EnvironmentHead Head,
        EnvironmentConfiguration Configuration,
        JsonElement? Catalog = null);

    private sealed record EnvironmentHead(
        EnvironmentConnectionReference Connection,
        long RowRevision,
        long CurrentConfigurationRevision,
        long? CurrentDiscoveryRevision,
        string State);

    private sealed record EnvironmentConnectionReference(string ProjectId, Guid ConnectionId);

    private sealed record EnvironmentConfiguration(
        EnvironmentConnectionReference? Connection,
        long ConfigurationRevision,
        string ConfigurationSha256,
        string EndpointUri,
        string? ResourceUri,
        string AuthenticationMode,
        string TransportProfile,
        string? IdentityBindingReference,
        JsonElement? RegistryServer = null);

    private sealed record ProjectSummaryProof(string ProjectId, long Revision, string State);
}
