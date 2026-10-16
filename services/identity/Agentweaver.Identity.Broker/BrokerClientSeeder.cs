using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Reconciles the operator-declared client list from <see cref="IdentityBrokerOptions"/>
/// against OpenIddict's persisted applications at startup. A client is created the first
/// time it is seen. An existing client whose persisted redirect URIs, scopes, or type no
/// longer match the declared configuration fails startup rather than being silently
/// mutated: changing a registered client's trust boundary is an explicit, reviewed act.
/// </summary>
public sealed class BrokerClientSeeder
{
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IdentityBrokerOptions _options;

    public BrokerClientSeeder(IOpenIddictApplicationManager applications, IdentityBrokerOptions options)
    {
        _applications = applications;
        _options = options;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _options.Clients)
        {
            ValidateClient(client);

            var existing = await _applications.FindByClientIdAsync(client.ClientId, cancellationToken);
            var descriptor = BuildDescriptor(client);

            if (existing is null)
            {
                await _applications.CreateAsync(descriptor, cancellationToken);
                continue;
            }

            if (!await MatchesAsync(existing, descriptor, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Client '{client.ClientId}' is already registered with different redirect URIs, " +
                    "scopes, credentials, resources, or type. Startup refuses to mutate a registered client's trust " +
                    "boundary; remove or deliberately migrate it through an explicit administrative path.");
            }
        }
    }

    private static void ValidateClient(BrokerClientOptions client)
    {
        foreach (var redirect in client.RedirectUris)
        {
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps &&
                !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidOperationException(
                    $"Client '{client.ClientId}' has an invalid redirect URI '{redirect}': only absolute " +
                    "HTTPS URIs (or HTTP loopback for native/test clients) without a fragment are accepted.");
            }
        }

        if (client.Type == BrokerClientType.Confidential && string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            throw new InvalidOperationException(
                $"Confidential client '{client.ClientId}' requires a client secret.");
        }

        if (client.Type == BrokerClientType.Public && !string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            throw new InvalidOperationException(
                $"Public client '{client.ClientId}' must not declare a client secret.");
        }
    }

    private static OpenIddictApplicationDescriptor BuildDescriptor(BrokerClientOptions client)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            ClientSecret = client.Type == BrokerClientType.Confidential ? client.ClientSecret : null,
            ClientType = client.Type == BrokerClientType.Confidential
                ? ClientTypes.Confidential
                : ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = client.DisplayName,
        };

        foreach (var redirect in client.RedirectUris)
            descriptor.RedirectUris.Add(new Uri(redirect, UriKind.Absolute));

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        if (client.Scopes.Contains(Scopes.OfflineAccess, StringComparer.Ordinal))
            descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        foreach (var scope in client.Scopes)
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);

        // Audiences ("resources") are bound to scopes, not applications, in OpenIddict's
        // data model; see BrokerScopeSeeder for how client.Resources is reconciled.
        return descriptor;
    }

    private async Task<bool> MatchesAsync(
        object existingApplication, OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
    {
        var existing = new OpenIddictApplicationDescriptor();
        await _applications.PopulateAsync(existing, existingApplication, cancellationToken);

        return existing.ClientType == descriptor.ClientType &&
            existing.ConsentType == descriptor.ConsentType &&
            (descriptor.ClientType == ClientTypes.Public
                ? string.IsNullOrEmpty(existing.ClientSecret)
                : await _applications.ValidateClientSecretAsync(existingApplication, descriptor.ClientSecret!, cancellationToken)) &&
            new HashSet<Uri>(existing.RedirectUris).SetEquals(descriptor.RedirectUris) &&
            new HashSet<string>(existing.Permissions).SetEquals(descriptor.Permissions) &&
            new HashSet<string>(existing.Requirements).SetEquals(descriptor.Requirements);
    }
}
