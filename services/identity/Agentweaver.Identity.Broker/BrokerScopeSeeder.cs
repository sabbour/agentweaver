using OpenIddict.Abstractions;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Reconciles OAuth scopes and their bound resource-server audiences against OpenIddict's
/// persisted scope store. In OpenIddict's data model, audiences ("resources") are attributes
/// of a <em>scope</em>, not of an application: a client requesting a scope causes the token
/// to carry whichever audiences are registered on that scope. This seeder aggregates, across
/// every operator-declared client, the resources associated with each scope name, and fails
/// closed (same as <see cref="BrokerClientSeeder"/>) if a scope is already registered with a
/// different audience set than configured.
/// </summary>
public sealed class BrokerScopeSeeder(IOpenIddictScopeManager scopes, IdentityBrokerOptions options)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var resourcesByScope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var client in options.Clients)
        {
            foreach (var scope in client.Scopes)
            {
                if (!resourcesByScope.TryGetValue(scope, out var resources))
                    resourcesByScope[scope] = resources = new HashSet<string>(StringComparer.Ordinal);

                foreach (var resource in client.Resources)
                    resources.Add(resource);
            }
        }

        foreach (var (name, resources) in resourcesByScope)
        {
            var descriptor = new OpenIddictScopeDescriptor { Name = name };
            foreach (var resource in resources)
                descriptor.Resources.Add(resource);

            var existing = await scopes.FindByNameAsync(name, cancellationToken);
            if (existing is null)
            {
                await scopes.CreateAsync(descriptor, cancellationToken);
                continue;
            }

            var current = new OpenIddictScopeDescriptor();
            await scopes.PopulateAsync(current, existing, cancellationToken);
            if (!current.Resources.SetEquals(descriptor.Resources))
            {
                throw new InvalidOperationException(
                    $"Scope '{name}' is already registered with a different resource/audience set " +
                    $"({string.Join(", ", current.Resources)}) than configured " +
                    $"({string.Join(", ", descriptor.Resources)}). Startup refuses to mutate a registered " +
                    "scope's audience set; remove or deliberately migrate it through an explicit administrative path.");
            }
        }
    }
}
