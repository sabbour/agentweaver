using Agentweaver.Identity;

namespace Agentweaver.Identity.Broker;

public sealed class RuntimeCredentialPolicy
{
    public RuntimeCredentialPolicy(string issuer, TimeSpan bootstrapLifetime, TimeSpan sourceLifetime)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var endpoint) ||
            !RuntimeContractValidation.IsHttpsEndpoint(endpoint) ||
            bootstrapLifetime <= TimeSpan.Zero || sourceLifetime <= TimeSpan.Zero)
            throw new ArgumentException("An existing HTTPS issuer and explicit positive runtime lifetimes are required.");
        Issuer = endpoint.AbsoluteUri;
        BootstrapLifetime = bootstrapLifetime;
        SourceLifetime = sourceLifetime;
    }

    public string Issuer { get; }
    public TimeSpan BootstrapLifetime { get; }
    public TimeSpan SourceLifetime { get; }
}
