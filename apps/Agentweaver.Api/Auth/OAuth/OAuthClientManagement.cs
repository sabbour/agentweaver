using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthStaticClientReconciler(
    IServiceScopeFactory scopeFactory,
    OAuthServerConfiguration configuration,
    ILogger<OAuthStaticClientReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var configuredIds = configuration.StaticClients.Select(x => x.ClientId).ToHashSet(StringComparer.Ordinal);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver:oauth:static-clients'))",
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var client in configuration.StaticClients)
        {
            var application = await manager.FindByClientIdAsync(client.ClientId, cancellationToken).ConfigureAwait(false);
            var descriptor = CreateDescriptor(client, configuration.Resource.AbsoluteUri);
            if (application is null)
                await manager.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
            else
                await manager.UpdateAsync(application, descriptor, cancellationToken).ConfigureAwait(false);
        }

        await foreach (var application in manager.ListAsync(null, null, cancellationToken))
        {
            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, application, cancellationToken).ConfigureAwait(false);
            if (!IsManagedStatic(descriptor) || configuredIds.Contains(descriptor.ClientId!))
                continue;

            descriptor.Permissions.Clear();
            descriptor.RedirectUris.Clear();
            descriptor.DisplayName = $"Disabled: {descriptor.DisplayName}";
            descriptor.Properties["agentweaver_disabled"] = JsonSerializer.SerializeToElement(true);
            await manager.UpdateAsync(application, descriptor, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Disabled removed static OAuth client {ClientId}.", descriptor.ClientId);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static OpenIddictApplicationDescriptor CreateDescriptor(OAuthStaticClient client, string resource)
    {
        var descriptor = BaseDescriptor(client.ClientId, client.DisplayName, client.RedirectUris, client.Scopes, resource);
        descriptor.Properties["agentweaver_static"] = JsonSerializer.SerializeToElement(true);
        return descriptor;
    }

    internal static OpenIddictApplicationDescriptor BaseDescriptor(
        string clientId,
        string displayName,
        IEnumerable<string> redirects,
        IEnumerable<string> scopes,
        string resource)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = displayName,
        };
        foreach (var redirect in redirects)
            descriptor.RedirectUris.Add(new Uri(redirect, UriKind.Absolute));
        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.Endpoints.Revocation,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
        ]);
        foreach (var scope in scopes.Where(x => x != Scopes.OfflineAccess))
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        descriptor.Permissions.Add(Permissions.Prefixes.Resource + resource);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        return descriptor;
    }

    private static bool IsManagedStatic(OpenIddictApplicationDescriptor descriptor) =>
        descriptor.Properties.TryGetValue("agentweaver_static", out var value)
        && value.ValueKind == JsonValueKind.True;
}

public sealed class OAuthDynamicClientRegistrationService(
    MemoryDbContext db,
    IOpenIddictApplicationManager manager,
    OAuthServerConfiguration configuration)
{
    private static readonly HashSet<string> AllowedProperties =
    [
        "redirect_uris", "client_name", "token_endpoint_auth_method",
        "grant_types", "response_types", "scope"
    ];

    public async Task<OAuthRegistrationResponse> RegisterAsync(
        JsonElement document,
        string source,
        CancellationToken ct)
    {
        if (document.ValueKind != JsonValueKind.Object
            || document.EnumerateObject().Any(property => !AllowedProperties.Contains(property.Name)))
            throw new OAuthRegistrationException("invalid_client_metadata", "Unsupported client metadata.");

        var redirects = ReadStringArray(document, "redirect_uris");
        if (redirects is { Length: 1 }
            && string.Equals(
                redirects[0],
                OAuthKnownClients.ClaudeHostedRedirectUri,
                StringComparison.Ordinal))
        {
            throw new OAuthRegistrationException(
                "invalid_redirect_uri",
                $"Claude hosted connectors must use OAuth Client ID '{OAuthKnownClients.ClaudeHostedClientId}' with no client secret.");
        }
        if (redirects is not { Length: > 0 and <= 10 }
            || redirects.Distinct(StringComparer.Ordinal).Count() != redirects.Length
            || redirects.Any(uri => !OAuthRedirectUriValidator.IsValid(
                uri, allowDynamicLoopbackPort: true, allowHttps: false)))
            throw new OAuthRegistrationException("invalid_redirect_uri", "Redirect URIs must be exact native callbacks.");

        var name = ReadString(document, "client_name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new OAuthRegistrationException("invalid_client_metadata", "client_name is required and limited to 200 characters.");

        var method = ReadString(document, "token_endpoint_auth_method") ?? ClientAuthenticationMethods.None;
        var grants = ReadStringArray(document, "grant_types") ?? [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken];
        var responses = ReadStringArray(document, "response_types") ?? [ResponseTypes.Code];
        if (method != ClientAuthenticationMethods.None
            || grants.Length is < 1 or > 2
            || grants.Any(x => x is not GrantTypes.AuthorizationCode and not GrantTypes.RefreshToken)
            || !grants.Contains(GrantTypes.AuthorizationCode, StringComparer.Ordinal)
            || responses.Length != 1
            || responses[0] != ResponseTypes.Code)
            throw new OAuthRegistrationException("invalid_client_metadata", "Only public authorization-code clients are supported.");

        var scopes = (ReadString(document, "scope") ?? OAuthServerConfiguration.McpScope)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Length is < 1 or > 2
            || scopes.Any(x => x is not OAuthServerConfiguration.McpScope and not Scopes.OfflineAccess))
            throw new OAuthRegistrationException("invalid_client_metadata", "Requested scope is not allowed.");

        var sourceHash = OAuthCertificateLoader.HashOpaque(source);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver:oauth:dcr'))", ct)
                .ConfigureAwait(false);
        }
        var now = DateTimeOffset.UtcNow;
        await OAuthDynamicClientLifecycle.DisableExpiredAsync(
            db, manager, now, ct).ConfigureAwait(false);
        var since = now.AddDays(-1);
        var sourceRegistrations = await db.OAuthDynamicRegistrations.AsNoTracking()
            .Where(x => x.DisabledAt == null && x.SourceHash == sourceHash)
            .Select(x => x.RegisteredAt)
            .ToListAsync(ct).ConfigureAwait(false);
        if (sourceRegistrations.Count(x => x >= since) >= configuration.DynamicRegistrationsPerDay
            || await db.OAuthDynamicRegistrations.CountAsync(x => x.DisabledAt == null, ct).ConfigureAwait(false)
                >= configuration.DynamicRegistrationsTotal)
            throw new OAuthRegistrationException("temporarily_unavailable", "Dynamic registration quota exceeded.");

        var clientId = $"aw_native_{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24))}";
        var descriptor = OAuthStaticClientReconciler.BaseDescriptor(
            clientId, name, redirects, scopes, configuration.Resource.AbsoluteUri);
        descriptor.Properties[OAuthDynamicClientExpiration.DynamicProperty] =
            JsonSerializer.SerializeToElement(true);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
            now.Add(configuration.DynamicRegistrationLifetime).ToUnixTimeSeconds());
        descriptor.Properties[OAuthDynamicClientExpiration.ExpirationProperty] =
            JsonSerializer.SerializeToElement(expiresAt.ToUnixTimeSeconds());
        await manager.CreateAsync(descriptor, ct).ConfigureAwait(false);
        db.OAuthDynamicRegistrations.Add(new OAuthDynamicRegistration
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            SourceHash = sourceHash,
            RegisteredAt = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new(
            clientId,
            name,
            redirects,
            method,
            grants,
            responses,
            string.Join(' ', scopes),
            now.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds());
    }

    private static string? ReadString(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[]? ReadStringArray(JsonElement document, string name)
    {
        if (!document.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            throw new OAuthRegistrationException("invalid_client_metadata", $"{name} must be an array.");
        var values = value.EnumerateArray().Select(x =>
            x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray();
        return values.Any(string.IsNullOrWhiteSpace)
            ? null
            : values.Select(value => value!).ToArray();
    }
}

public sealed record OAuthRegistrationResponse(
    string client_id,
    string client_name,
    string[] redirect_uris,
    string token_endpoint_auth_method,
    string[] grant_types,
    string[] response_types,
    string scope,
    long client_id_issued_at,
    long client_id_expires_at);

public sealed class OAuthRegistrationException(string error, string description) : Exception(description)
{
    public string Error { get; } = error;
}

internal static class OAuthDynamicClientLifecycle
{
    public static async Task<int> DisableExpiredAsync(
        MemoryDbContext db,
        IOpenIddictApplicationManager manager,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var active = await db.OAuthDynamicRegistrations
            .Where(x => x.DisabledAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var disabled = 0;

        foreach (var registration in active)
        {
            var application = await manager.FindByClientIdAsync(registration.ClientId, ct).ConfigureAwait(false);
            if (application is null)
                continue;

            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, application, ct).ConfigureAwait(false);
            if (!OAuthDynamicClientExpiration.HasExpired(descriptor, now))
                continue;

            descriptor.Permissions.Clear();
            descriptor.RedirectUris.Clear();
            descriptor.DisplayName = $"Expired: {descriptor.DisplayName}";
            descriptor.Properties["agentweaver_disabled"] = JsonSerializer.SerializeToElement(true);
            await manager.UpdateAsync(application, descriptor, ct).ConfigureAwait(false);
            registration.DisabledAt = now;
            disabled++;
        }

        if (disabled > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return disabled;
    }
}

internal static class OAuthDynamicClientExpiration
{
    internal const string DynamicProperty = "agentweaver_dynamic";
    internal const string ExpirationProperty = "agentweaver_expires_at";

    internal static bool HasExpired(OpenIddictApplicationDescriptor descriptor, DateTimeOffset now) =>
        IsDynamic(descriptor)
        && (!TryGetExpiration(descriptor, out var expiration) || expiration <= now);

    internal static bool IsDynamic(OpenIddictApplicationDescriptor descriptor) =>
        descriptor.Properties.TryGetValue(DynamicProperty, out var value)
        && value.ValueKind == JsonValueKind.True;

    internal static bool TryGetExpiration(
        OpenIddictApplicationDescriptor descriptor,
        out DateTimeOffset expiration)
    {
        expiration = default;
        if (!descriptor.Properties.TryGetValue(ExpirationProperty, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            try
            {
                expiration = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        if (value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out var legacyExpiration))
            return false;

        expiration = DateTimeOffset.FromUnixTimeSeconds(legacyExpiration.ToUnixTimeSeconds());
        return true;
    }
}
