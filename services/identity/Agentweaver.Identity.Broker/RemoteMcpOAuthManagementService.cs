using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;

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
public sealed record RemoteMcpOAuthDisconnectRequest(
    long ExpectedConnectionRevision,
    long ExpectedCredentialRevision,
    long ExpectedConfigurationRevision,
    Guid IdempotencyKey);

internal sealed record RemoteMcpOAuthConsentPreparationRequest(
    long ExpectedConnectionRevision,
    long ExpectedCredentialRevision,
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256);

internal sealed record RemoteMcpOAuthConsentPreparation(
    Guid CorrelationId,
    string State,
    string PkceChallenge,
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
    TimeProvider time,
    ISecretVersionWriter? secretWriter = null)
{
    private const string OAuthAuthenticationMode = "delegatedOAuth";
    private const string TransportProfile = "streamableHttp20250618";
    private static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Uri _projectsOwnerAddress =
        RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(options.ProjectsOwnerAddress));

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
                _projectsOwnerAddress, projects);
            receipt = await bindingClient.LinkAsync(
                actor,
                row.ProjectId,
                row.ConnectionId,
                new LinkRemoteMcpIdentityBindingRequest(
                    row.ConfigurationRevision,
                    row.EnvironmentConfigurationHash,
                    reference,
                    operationId),
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
        var correlationId = Guid.NewGuid();
        var material = RemoteMcpOAuthConsentMaterial.Create(
            binding, correlationId, time.GetUtcNow() + ConsentLifetime, time);
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
                Revision = transition.Consent.Revision,
                State = transition.Consent.State,
                CreatedAt = transition.Consent.CreatedAt,
                ExpiresAt = transition.Consent.ExpiresAt
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new RemoteMcpOAuthConsentPreparation(
                transition.Consent.CorrelationId,
                state,
                transition.Consent.PkceChallenge,
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
        CancellationToken cancellationToken)
    {
        if (!actor.Bearer.IsUsable() ||
            actor.TenantSelector is not null && actor.TenantSelector != row.TenantId)
            throw Denied("remote_mcp_owner_denied");

        var context = await RuntimeOwnerHttpTransport.SendAsync<ProjectAuthorizationContextResponse>(
            projects, _projectsOwnerAddress, "/api/authorization/context", actor, null, cancellationToken)
            .ConfigureAwait(false);
        if (context.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            context.Issuer != issuer || context.ActorId != actorId ||
            context.MembershipRevision <= 0 || context.EffectiveAuthority.IsDefault ||
            context.BoundProjectId is not null || context.BoundRunId is not null ||
            context.TenantId != row.TenantId ||
            !context.EffectiveAuthority.Any(resource =>
                resource.Permissions.Any(permission =>
                    permission.Permission == requiredPermission &&
                    permission.RoleRevision > 0 &&
                    (resource.ResourceType == ProjectAuthorityResourceType.Project &&
                        resource.ResourceId == row.ProjectId ||
                     resource.ResourceType == ProjectAuthorityResourceType.Tenant &&
                        resource.ResourceId == row.TenantId))) ||
            !actor.Bearer.IsUsable())
            throw Denied("remote_mcp_owner_denied");

        var project = await RuntimeOwnerHttpTransport.SendAsync<ProjectSummaryProof>(
            projects, _projectsOwnerAddress,
            $"/api/projects/{Uri.EscapeDataString(row.ProjectId)}", actor, null, cancellationToken)
            .ConfigureAwait(false);
        if (project.ProjectId != row.ProjectId || project.Revision <= 0 || project.State != "active" ||
            !actor.Bearer.IsUsable())
            throw Denied("remote_mcp_project_unavailable");
    }

    private async Task<CurrentConfiguration?> ReadCurrentConfigurationAsync(
        RuntimeActorAuthorization actor,
        RemoteMcpOAuthConnectionRecord row,
        CancellationToken cancellationToken)
    {
        var path = $"/api/projects/{Uri.EscapeDataString(row.ProjectId)}/remote-mcp/connections/{row.ConnectionId:D}";
        var snapshot = await RuntimeOwnerHttpTransport.ReadOptionalAsync<EnvironmentSnapshot>(
            projects, _projectsOwnerAddress, path, actor, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;
        if (snapshot.Head is null || snapshot.Head.Connection is null || snapshot.Configuration is null ||
            snapshot.Head.RowRevision <= 0 || snapshot.Head.CurrentConfigurationRevision <= 0 ||
            snapshot.Head.Connection.ConnectionId != row.ConnectionId ||
            snapshot.Head.Connection.ProjectId != row.ProjectId ||
            snapshot.Configuration.ConfigurationRevision != snapshot.Head.CurrentConfigurationRevision)
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");

        var configuration = await RuntimeOwnerHttpTransport.ReadOptionalAsync<EnvironmentConfiguration>(
            projects, _projectsOwnerAddress,
            $"{path}/configurations/{snapshot.Head.CurrentConfigurationRevision}",
            actor, cancellationToken).ConfigureAwait(false);
        if (configuration is null)
            return null;
        if (configuration.ConfigurationRevision != snapshot.Head.CurrentConfigurationRevision ||
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

    private static bool IsCanonicalHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsoluteUri == value;

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
        return configuration.ConnectionState.Equals("active", StringComparison.OrdinalIgnoreCase) &&
            configuration.EndpointUri == row.EndpointUri &&
            configuration.ResourceUri == row.ResourceUri &&
            configuration.AuthenticationMode == OAuthAuthenticationMode &&
            configuration.TransportProfile == TransportProfile &&
            row.TransportProfile == RemoteMcpOAuthConnectionBinding.SupportedTransportProfile &&
            (isPreLink || isReplay);
    }

    private static bool MatchesReceipt(
        RemoteMcpOAuthConnectionRecord row,
        CurrentConfiguration configuration,
        RemoteMcpIdentityBindingReceipt receipt) =>
        configuration.ConnectionState.Equals("active", StringComparison.OrdinalIgnoreCase) &&
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
        receipt.IdentityBindingReference == row.IdentityBindingReference;

    private static RemoteMcpOAuthConnectionBinding CreateLinkedBinding(
        RemoteMcpOAuthConnectionRecord row,
        RemoteMcpIdentityBindingReceipt receipt)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(row.ScopesJson, Json)
            ?? throw new InvalidOperationException("The stored OAuth scope registration is invalid.");
        return new RemoteMcpOAuthConnectionBinding(
            row.OwnerId.ToString("D"),
            row.TenantId,
            row.ProjectId,
            row.ConnectionId,
            receipt.FinalConfigurationRevision,
            receipt.FinalConfigurationSha256,
            receipt.IdentityBindingReference,
            new Uri(row.EndpointUri),
            new Uri(row.ResourceUri),
            new Uri(row.IssuerUri),
            new Uri(row.RedirectUri),
            row.TransportProfile,
            scopes);
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
