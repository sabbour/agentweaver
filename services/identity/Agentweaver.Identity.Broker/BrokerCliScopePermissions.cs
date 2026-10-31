using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

internal static class BrokerCliScopePermissions
{
    public const string ClientId = "agentweaver-cli";
    public const string AllRegisteredScopes = "agentweaver:scope:all-registered";
    public const string RedirectUri = "http://127.0.0.1:49173/oauth/callback";

    private static readonly string[] ExistingScopes = [Scopes.OpenId, "api.read", Scopes.OfflineAccess];
    private static readonly HashSet<string> RequiredScopes = new(ExistingScopes, StringComparer.Ordinal);

    public static bool HasRequiredScopes(IEnumerable<string> scopes) =>
        RequiredScopes.IsSubsetOf(scopes);

    public static OpenIddictApplicationDescriptor CreateDescriptor(
        BrokerClientOptions client, bool allowAllRegisteredScopes)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            ClientSecret = null,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = client.DisplayName,
        };
        foreach (var redirect in client.RedirectUris)
            descriptor.RedirectUris.Add(new Uri(redirect, UriKind.Absolute));

        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            .. ExistingScopes.Select(scope => Permissions.Prefixes.Scope + scope),
        ]);
        if (allowAllRegisteredScopes)
            descriptor.Permissions.Add(AllRegisteredScopes);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        return descriptor;
    }

    public static OpenIddictApplicationDescriptor CreatePreviousDescriptor() =>
        CreateDescriptor(CreateOptions(), allowAllRegisteredScopes: false);

    public static OpenIddictApplicationDescriptor CreateMigratedDescriptor() =>
        CreateDescriptor(CreateOptions(), allowAllRegisteredScopes: true);

    public static async Task MigrateAsync(
        IOpenIddictApplicationManager applications, CancellationToken cancellationToken)
    {
        var application = await applications.FindByClientIdAsync(ClientId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Client '{ClientId}' is missing. Startup will not create its all-registered-scopes permission.");
        var current = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(current, application, cancellationToken);

        var previous = CreatePreviousDescriptor();
        var migrated = CreateMigratedDescriptor();
        if (Matches(current, migrated))
            return;
        if (!Matches(current, previous))
            throw new InvalidOperationException(
                $"Client '{ClientId}' does not match the exact prior public CLI registration. " +
                "No permissions were changed.");

        current.Permissions.Add(AllRegisteredScopes);
        await applications.UpdateAsync(application, current, cancellationToken);
    }

    private static BrokerClientOptions CreateOptions() => new()
    {
        ClientId = ClientId,
        DisplayName = "Agentweaver CLI",
        Type = BrokerClientType.Public,
        RedirectUris = [RedirectUri],
        Scopes = ExistingScopes,
        Resources = ["https://api.test"],
    };

    private static bool Matches(
        OpenIddictApplicationDescriptor current, OpenIddictApplicationDescriptor expected) =>
        current.ClientType == expected.ClientType &&
        current.ConsentType == expected.ConsentType &&
        string.IsNullOrEmpty(current.ClientSecret) &&
        new HashSet<Uri>(current.RedirectUris).SetEquals(expected.RedirectUris) &&
        new HashSet<string>(current.Permissions).SetEquals(expected.Permissions) &&
        new HashSet<string>(current.Requirements).SetEquals(expected.Requirements);
}

internal static class BrokerScopePermissionHandlers
{
    public static void AddCliRegisteredScopePermissionFilters(OpenIddictServerOptions options)
    {
        ReplaceHandler<ValidateAuthorizationRequestContext, BrokerCliAuthorizationScopePermissionFilter>(
            options,
            OpenIddictServerHandlers.Authentication.ValidateScopePermissions.Descriptor);
        ReplaceHandler<ValidateTokenRequestContext, BrokerCliTokenScopePermissionFilter>(
            options,
            OpenIddictServerHandlers.Exchange.ValidateScopePermissions.Descriptor);
    }

    private static void ReplaceHandler<TContext, TFilter>(
        OpenIddictServerOptions options, OpenIddictServerHandlerDescriptor original)
        where TContext : BaseContext
        where TFilter : class, IOpenIddictServerHandlerFilter<TContext>
    {
        for (var index = 0; index < options.Handlers.Count; index++)
        {
            var handler = options.Handlers[index];
            if (handler.ContextType != typeof(TContext) ||
                handler.ServiceDescriptor.ServiceType != original.ServiceDescriptor.ServiceType)
            {
                continue;
            }

            options.Handlers[index] = OpenIddictServerHandlerDescriptor.CreateBuilder<TContext>()
                .Import(original)
                .AddFilter<TFilter>()
                .Build();
            return;
        }

        throw new InvalidOperationException(
            $"OpenIddict's {typeof(TContext).Name} scope-permission handler is missing.");
    }

    internal static async ValueTask<bool> IsScopePermissionCheckEnabledAsync(
        string? clientId, IOpenIddictApplicationManager applications, CancellationToken cancellationToken)
    {
        if (!string.Equals(clientId, BrokerCliScopePermissions.ClientId, StringComparison.Ordinal))
            return true;

        var application = await applications.FindByClientIdAsync(
            BrokerCliScopePermissions.ClientId, cancellationToken);
        return application is null ||
            !await applications.HasPermissionAsync(
                application, BrokerCliScopePermissions.AllRegisteredScopes, cancellationToken);
    }
}

internal sealed class BrokerCliAuthorizationScopePermissionFilter(
    IOpenIddictApplicationManager applications) :
    IOpenIddictServerHandlerFilter<ValidateAuthorizationRequestContext>
{
    public ValueTask<bool> IsActiveAsync(ValidateAuthorizationRequestContext context) =>
        BrokerScopePermissionHandlers.IsScopePermissionCheckEnabledAsync(
            context.ClientId, applications, context.CancellationToken);
}

internal sealed class BrokerCliTokenScopePermissionFilter(
    IOpenIddictApplicationManager applications) :
    IOpenIddictServerHandlerFilter<ValidateTokenRequestContext>
{
    public ValueTask<bool> IsActiveAsync(ValidateTokenRequestContext context) =>
        BrokerScopePermissionHandlers.IsScopePermissionCheckEnabledAsync(
            context.ClientId, applications, context.CancellationToken);
}
