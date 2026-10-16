using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

public sealed record CopilotConnectionOptions(
    Uri ProjectsOwnerAddress, string ClientId, Uri CallbackUri, SecretRef ClientSecretReference);

public sealed record CopilotConnectionReceipt(
    Guid ConnectionId, long Revision, ProjectAuthorityResourceType Scope,
    string ScopeId, CopilotConnectionState State, DateTimeOffset FreshUntil);

public sealed record CopilotConnectionStart(CopilotConnectionReceipt Connection, Uri AuthorizationUri)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string CallbackCookie { get; init; } = string.Empty;
}

internal sealed record CopilotLinkState(
    Guid ConnectionId, string ActorId, string Verifier, string CallbackCookieHash, DateTimeOffset ExpiresAt);

internal sealed record CopilotUserCredential(
    string Status, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public override string ToString() => nameof(CopilotUserCredential) + " [REDACTED]";
}

public sealed class CopilotConnectionAuthority(
    IdentityBrokerDbContext db, CopilotConnectionOptions options,
    HttpClient projects, HttpClient github, ISecretRedemption secrets, ISecretVersionWriter writer,
    IDataProtectionProvider protection, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _stateProtector = protection.CreateProtector(
        "Agentweaver.Identity.CopilotUserConnection.v1");

    public async Task<CopilotConnectionStart> BeginAsync(
        RuntimeActorAuthorization actor, string issuer, string actorId,
        ProjectAuthorityResourceType scope, string scopeId, CancellationToken cancellationToken)
    {
        if (scope is not (ProjectAuthorityResourceType.Project or ProjectAuthorityResourceType.Platform) ||
            scope == ProjectAuthorityResourceType.Platform && scopeId != "default")
            throw Denied("copilot_connection_scope_invalid");
        RuntimeContractValidation.ValidateIdentifier(scopeId);
        var authority = await RequireWriteAuthorityAsync(
            actor, issuer, actorId, scope, scopeId, cancellationToken);
        var id = Guid.NewGuid();
        var expiry = Now().AddMinutes(5);
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var callbackCookie = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var state = _stateProtector.Protect(JsonSerializer.Serialize(
            new CopilotLinkState(id, actorId, verifier, Hash(callbackCookie), expiry), Json));
        var row = new CopilotConnectionRecord
        {
            ConnectionId = id, OwnerIssuer = issuer, OwnerActorId = actorId,
            TenantId = authority.TenantId, Scope = scope, ScopeId = scopeId, Revision = 1,
            State = CopilotConnectionState.Pending, FreshUntil = expiry, StateHash = Hash(state)
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.CopilotConnections.Add(row);
        AddRevision(row, row.Revision, row.State);
        await RequireWriteAuthorityAsync(actor, issuer, actorId, scope, scopeId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RequireWriteAuthorityAsync(actor, issuer, actorId, scope, scopeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = new Uri("https://github.com/login/oauth/authorize?" +
            $"client_id={Uri.EscapeDataString(options.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(options.CallbackUri.AbsoluteUri)}" +
            $"&state={Uri.EscapeDataString(state)}&code_challenge={challenge}&code_challenge_method=S256");
        return new(Receipt(row), url) { CallbackCookie = callbackCookie };
    }

    public async Task<CopilotConnectionReceipt> CompleteAsync(
        RuntimeActorAuthorization actor, string issuer, string actorId,
        string state, string? code, string? callbackCookie, string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 8192 ||
            error is null && (string.IsNullOrWhiteSpace(code) || code.Length > 2048 || code.Any(char.IsWhiteSpace)) ||
            error is not null && (error != "access_denied" || code is not null) ||
            callbackCookie is null || callbackCookie.Length != 64)
            throw Denied("copilot_connection_callback_invalid");
        CopilotLinkState link;
        try
        {
            link = JsonSerializer.Deserialize<CopilotLinkState>(_stateProtector.Unprotect(state), Json)
                ?? throw Denied("copilot_connection_callback_invalid");
        }
        catch (Exception failure) when (failure is CryptographicException or JsonException)
        {
            throw Denied("copilot_connection_callback_invalid");
        }
        if (link.ActorId != actorId || link.ExpiresAt <= time.GetUtcNow() ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(link.CallbackCookieHash), SHA256.HashData(Encoding.UTF8.GetBytes(callbackCookie))))
            throw Denied("copilot_connection_callback_expired");
        var row = await ReadAsync(link.ConnectionId, cancellationToken);
        RequireOwner(row, issuer, actorId);
        if (row.State != CopilotConnectionState.Pending || row.StateHash != Hash(state) ||
            row.FreshUntil <= time.GetUtcNow())
            throw Denied("copilot_connection_callback_replayed");
        await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
        await TransitionAsync(row, CopilotConnectionState.Refreshing, cancellationToken,
            token => RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, token));
        if (error is not null)
        {
            await TransitionAsync(row, CopilotConnectionState.ReconnectRequired, cancellationToken,
                token => RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, token));
            throw Denied("copilot_connection_consent_denied");
        }
        return await RotateAsync(row, actor, issuer, actorId,
            new Dictionary<string, string>
            {
                ["code"] = code!, ["redirect_uri"] = options.CallbackUri.AbsoluteUri,
                ["code_verifier"] = link.Verifier
            }, null, cancellationToken);
    }

    public async Task<CopilotConnectionReceipt> ReadStatusAsync(
        RuntimeActorAuthorization actor, string issuer, string actorId,
        Guid connectionId, CancellationToken cancellationToken)
    {
        var row = await ReadAsync(connectionId, cancellationToken);
        RequireOwner(row, issuer, actorId);
        await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
        var current = await ReadAsync(connectionId, cancellationToken);
        return Receipt(current);
    }

    public async Task<CopilotConnectionReceipt> RefreshAsync(
        RuntimeActorAuthorization actor, string issuer, string actorId,
        Guid connectionId, long expectedRevision, CancellationToken cancellationToken)
    {
        var row = await ReadAsync(connectionId, cancellationToken);
        RequireOwner(row, issuer, actorId);
        if (row.Revision != expectedRevision ||
            row.State is not (CopilotConnectionState.Connected or CopilotConnectionState.TransientUnavailable) ||
            row.SecretId is null || row.SecretVersion is null || row.GitHubUserId is null)
            throw Denied("copilot_connection_refresh_conflict");
        await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
        await TransitionAsync(row, CopilotConnectionState.Refreshing, cancellationToken,
            token => RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, token));
        SecretCredential? current = null;
        try
        {
            current = await secrets.RedeemAsync(new SecretRedemptionRequest(
                new SecretRef(row.SecretId, row.SecretVersion), "copilot-connection-refresh",
                connectionId.ToString("D")), cancellationToken);
            var stored = JsonSerializer.Deserialize<CopilotUserCredential>(current.GetValue(), Json);
            if (stored is null || stored.Status != "signed-in" ||
                string.IsNullOrWhiteSpace(stored.RefreshToken) ||
                stored.RefreshTokenExpiresAt <= time.GetUtcNow())
                throw Denied("copilot_connection_reconnect_required");
            await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
            return await RotateAsync(row, actor, issuer, actorId,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = stored.RefreshToken
                }, row.GitHubUserId, cancellationToken);
        }
        catch
        {
            if (row.State == CopilotConnectionState.Refreshing)
                await MarkFailureAsync(row, CopilotConnectionState.TransientUnavailable);
            throw;
        }
        finally
        {
            current?.Invalidate();
        }
    }

    public async Task<CopilotConnectionReceipt> RevokeAsync(
        RuntimeActorAuthorization actor, string issuer, string actorId,
        Guid connectionId, long expectedRevision, CancellationToken cancellationToken)
    {
        var row = await ReadAsync(connectionId, cancellationToken);
        RequireOwner(row, issuer, actorId);
        if (row.Revision != expectedRevision || row.State == CopilotConnectionState.Revoked)
            throw Denied("copilot_connection_revision_stale");
        await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
        await TransitionAsync(row, CopilotConnectionState.Revoked, cancellationToken,
            token => RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, token));
        return Receipt(row);
    }

    internal async Task<CopilotConnectionRecord> RequireCurrentAsync(
        RuntimeBinding binding, CancellationToken cancellationToken)
    {
        if (binding.ModelSourceMode != ModelSourceMode.HostedCopilot ||
            binding.ModelConnectionId is not { } id || binding.ModelCredentialReference is not null)
            throw Denied("runtime_copilot_selection_migration_required");
        var row = await ReadAsync(id, cancellationToken);
        if (row.State != CopilotConnectionState.Connected || row.FreshUntil <= time.GetUtcNow() ||
            row.OwnerIssuer != binding.ActorIssuer || row.Scope != binding.ModelConnectionScope ||
            row.CredentialKind != RuntimeModelCredentialKind.GitHubUserAccess ||
            row.Scope == ProjectAuthorityResourceType.Project &&
                (row.TenantId != binding.TenantId || row.ScopeId != binding.ProjectId) ||
            row.Scope == ProjectAuthorityResourceType.Platform && row.ScopeId != "default" ||
            row.GitHubUserId is null || row.SecretId is null || row.SecretVersion is null)
            throw Denied("runtime_copilot_connection_unavailable");
        return row;
    }

    private async Task<CopilotConnectionReceipt> RotateAsync(
        CopilotConnectionRecord row, RuntimeActorAuthorization actor, string issuer, string actorId,
        Dictionary<string, string> parameters, string? expectedUserId, CancellationToken cancellationToken)
    {
        SecretCredential? clientSecret = null;
        SecretCredential? next = null;
        var failureState = CopilotConnectionState.TransientUnavailable;
        try
        {
            clientSecret = await secrets.RedeemAsync(new SecretRedemptionRequest(
                options.ClientSecretReference, "copilot-connection-oauth",
                row.ConnectionId.ToString("D")), cancellationToken);
            parameters["client_id"] = options.ClientId;
            parameters["client_secret"] = clientSecret.GetValue();
            await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
            await RequireClaimAsync(row, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://github.com/login/oauth/access_token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(parameters);
            failureState = CopilotConnectionState.RefreshIndeterminate;
            using var response = await github.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                failureState = CopilotConnectionState.TransientUnavailable;
                if ((int)response.StatusCode is 400 or 401 or 403)
                {
                    using var rejected = await ReadJsonAsync(response, cancellationToken);
                    if (rejected.RootElement.TryGetProperty("error", out var rejection) &&
                        rejection.ValueKind == JsonValueKind.String &&
                        rejection.GetString() is "bad_refresh_token" or "invalid_grant" or "expired_token")
                    {
                        failureState = CopilotConnectionState.ReconnectRequired;
                        throw Denied("copilot_connection_refresh_rejected");
                    }
                }
                throw Denied("copilot_connection_exchange_failed");
            }
            if (response.RequestMessage?.RequestUri != request.RequestUri)
                throw Denied("copilot_connection_exchange_failed");
            using var body = await ReadJsonAsync(response, cancellationToken);
            var root = body.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                failureState = error.ValueKind == JsonValueKind.String &&
                    error.GetString() is "bad_refresh_token" or "invalid_grant" or "expired_token"
                    ? CopilotConnectionState.ReconnectRequired : CopilotConnectionState.TransientUnavailable;
                throw Denied(failureState == CopilotConnectionState.ReconnectRequired
                    ? "copilot_connection_refresh_rejected" : "copilot_connection_exchange_failed");
            }
            var access = ReadString(root, "access_token");
            var refresh = ReadString(root, "refresh_token");
            if (ReadString(root, "token_type") != "bearer" ||
                !access.StartsWith("ghu_", StringComparison.Ordinal) ||
                !root.TryGetProperty("expires_in", out var lifetime) ||
                lifetime.ValueKind != JsonValueKind.Number ||
                !lifetime.TryGetInt32(out var seconds) || seconds <= 0 || seconds > 86400 ||
                !root.TryGetProperty("refresh_token_expires_in", out var refreshLifetime) ||
                refreshLifetime.ValueKind != JsonValueKind.Number ||
                !refreshLifetime.TryGetInt32(out var refreshSeconds) || refreshSeconds <= seconds ||
                refreshSeconds > 366 * 86400)
                throw Denied("copilot_connection_user_credential_invalid");
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            userRequest.Headers.UserAgent.ParseAdd("Agentweaver.Identity.Broker");
            using var userResponse = await github.SendAsync(userRequest, cancellationToken);
            if (!userResponse.IsSuccessStatusCode ||
                userResponse.RequestMessage?.RequestUri != userRequest.RequestUri)
                throw Denied("copilot_connection_user_verification_failed");
            using var userBody = await ReadJsonAsync(userResponse, cancellationToken);
            if (!userBody.RootElement.TryGetProperty("id", out var userIdValue) ||
                userIdValue.ValueKind != JsonValueKind.Number ||
                !userIdValue.TryGetInt64(out var userId) || userId <= 0 ||
                ReadString(userBody.RootElement, "type") != "User")
                throw Denied("copilot_connection_user_verification_failed");
            var userIdText = userId.ToString(CultureInfo.InvariantCulture);
            if (expectedUserId is not null && expectedUserId != userIdText)
                throw Denied("copilot_connection_identity_changed");
            await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
            await RequireClaimAsync(row, cancellationToken);
            var now = Now();
            var stored = new CopilotUserCredential("signed-in", access, refresh,
                now.AddSeconds(seconds), now.AddSeconds(refreshSeconds));
            next = new SecretCredential(JsonSerializer.Serialize(stored, Json), stored.RefreshTokenExpiresAt, time);
            var reference = await writer.WriteVersionAsync(
                $"copilot-user-{row.ConnectionId:N}", next, cancellationToken);
            await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
            await RequireClaimAsync(row, cancellationToken);
            row.GitHubUserId = userIdText;
            row.SecretId = reference.Id;
            row.SecretVersion = reference.Version;
            row.FreshUntil = stored.ExpiresAt;
            await TransitionAsync(row, CopilotConnectionState.Connected, cancellationToken,
                token => RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, token));
            await RequireWriteAuthorityAsync(actor, issuer, actorId, row.Scope, row.ScopeId, cancellationToken);
            return Receipt(row);
        }
        catch
        {
            // A refresh may have consumed its upstream token. Never replay that rotation.
            await MarkFailureAsync(row, failureState);
            throw;
        }
        finally
        {
            clientSecret?.Invalidate();
            next?.Invalidate();
            parameters.Clear();
        }
    }

    private async Task<ProjectAuthorizationContextResponse> RequireWriteAuthorityAsync(
        RuntimeActorAuthorization actor, string issuer, string actorId,
        ProjectAuthorityResourceType scope, string scopeId, CancellationToken cancellationToken)
    {
        if (!actor.Bearer.IsUsable())
            throw Denied("copilot_connection_actor_expired");
        var context = await RuntimeOwnerHttpTransport.SendAsync<ProjectAuthorizationContextResponse>(
            projects, options.ProjectsOwnerAddress, "/api/authorization/context", actor, null, cancellationToken);
        if (context.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            context.Issuer != issuer || context.ActorId != actorId ||
            context.MembershipRevision <= 0 || context.EffectiveAuthority.IsDefault ||
            context.BoundProjectId is not null || context.BoundRunId is not null ||
            actor.TenantSelector is not null && actor.TenantSelector != context.TenantId ||
            !context.EffectiveAuthority.Any(resource =>
                resource.Permissions.Any(permission => permission.RoleRevision > 0 &&
                    (scope == ProjectAuthorityResourceType.Platform &&
                        resource.ResourceType == ProjectAuthorityResourceType.Platform &&
                        resource.ResourceId == scopeId &&
                        permission.Permission == ProjectAuthorizationPermission.WritePlatformRuntimeDefaults ||
                     scope == ProjectAuthorityResourceType.Project &&
                        permission.Permission == ProjectAuthorizationPermission.WriteProjects &&
                        (resource.ResourceType == ProjectAuthorityResourceType.Project &&
                            resource.ResourceId == scopeId ||
                         resource.ResourceType == ProjectAuthorityResourceType.Tenant &&
                            resource.ResourceId == context.TenantId)))) ||
            !actor.Bearer.IsUsable())
            throw Denied("copilot_connection_owner_denied");
        if (scope == ProjectAuthorityResourceType.Project)
        {
            var project = await RuntimeOwnerHttpTransport.SendAsync<ProjectSummaryProof>(
                projects, options.ProjectsOwnerAddress, $"/api/projects/{Uri.EscapeDataString(scopeId)}",
                actor, null, cancellationToken);
            if (project.ProjectId != scopeId || project.Revision <= 0 || project.State != "active")
                throw Denied("copilot_connection_project_unavailable");
        }
        return context;
    }

    private async Task<CopilotConnectionRecord> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        id == Guid.Empty ? throw Denied("copilot_connection_unknown") :
        await db.CopilotConnections.AsNoTracking().SingleOrDefaultAsync(
            row => row.ConnectionId == id, cancellationToken) ?? throw Denied("copilot_connection_unknown");

    private async Task RequireClaimAsync(CopilotConnectionRecord expected, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(expected.ConnectionId, cancellationToken);
        if (current.Revision != expected.Revision || current.State != CopilotConnectionState.Refreshing)
            throw Denied("copilot_connection_revision_stale");
    }

    private async Task TransitionAsync(
        CopilotConnectionRecord row, CopilotConnectionState state, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? revalidate = null)
    {
        var revision = row.Revision;
        var nextRevision = checked(revision + 1);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await db.CopilotConnections.Where(current =>
            current.ConnectionId == row.ConnectionId && current.Revision == revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(current => current.Revision, nextRevision)
                .SetProperty(current => current.State, state)
                .SetProperty(current => current.GitHubUserId, row.GitHubUserId)
                .SetProperty(current => current.SecretId, row.SecretId)
                .SetProperty(current => current.SecretVersion, row.SecretVersion)
                .SetProperty(current => current.FreshUntil, row.FreshUntil), cancellationToken);
        if (changed != 1)
            throw Denied("copilot_connection_revision_stale");
        AddRevision(row, nextRevision, state);
        await db.SaveChangesAsync(cancellationToken);
        if (revalidate is not null)
            await revalidate(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        row.Revision = nextRevision;
        row.State = state;
    }

    private async Task MarkFailureAsync(CopilotConnectionRecord row, CopilotConnectionState state)
    {
        db.ChangeTracker.Clear();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var current = await ReadAsync(row.ConnectionId, cleanup.Token);
        if (current.Revision == row.Revision &&
            current.State is CopilotConnectionState.Refreshing or CopilotConnectionState.Connected)
            await TransitionAsync(current, state, cleanup.Token);
    }

    private void AddRevision(CopilotConnectionRecord row, long revision, CopilotConnectionState state) =>
        db.CopilotConnectionRevisions.Add(new()
    {
        ConnectionId = row.ConnectionId, Revision = revision, State = state,
        SecretId = row.SecretId, SecretVersion = row.SecretVersion, FreshUntil = row.FreshUntil,
        RecordedAt = Now()
    });

    private static void RequireOwner(CopilotConnectionRecord row, string issuer, string actorId)
    {
        if (row.OwnerIssuer != issuer || row.OwnerActorId != actorId)
            throw Denied("copilot_connection_owner_denied");
    }

    private static CopilotConnectionReceipt Receipt(CopilotConnectionRecord row) =>
        new(row.ConnectionId, row.Revision, row.Scope, row.ScopeId, row.State, row.FreshUntil);
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }
    private static string Hash(string value) => RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(value));
    private static RuntimeAuthorizationException Denied(string code) => new(code);
    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) || property.GetString()!.Any(char.IsWhiteSpace))
            throw Denied("copilot_connection_upstream_contract_invalid");
        return property.GetString()!;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
        try
        {
            var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Denied("copilot_connection_upstream_contract_invalid");
            }
            return document;
        }
        catch (JsonException)
        {
            throw Denied("copilot_connection_upstream_contract_invalid");
        }
    }

    private sealed record ProjectSummaryProof(
        string ProjectId, string Name, string State, long Revision, long ConfigurationRevision,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
