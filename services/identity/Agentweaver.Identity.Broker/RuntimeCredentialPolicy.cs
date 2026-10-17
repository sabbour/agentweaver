using Agentweaver.Identity;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Holds the broker issuer and maximum lifetimes used when it creates bootstrap and
/// observation credentials for registered runtime instances.
/// </summary>
public sealed class RuntimeCredentialPolicy
{
    /// <summary>
    /// Creates a policy only for an absolute HTTPS issuer and positive explicit lifetimes.
    /// Invalid values fail construction rather than selecting defaults.
    /// </summary>
    /// <param name="issuer">Canonical issuer URI embedded in runtime-grant records.</param>
    /// <param name="bootstrapLifetime">Maximum lifetime for a bootstrap credential.</param>
    /// <param name="sourceLifetime">Maximum lifetime for a source-observation credential.</param>
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

    /// <summary>Maximum configured lifetime for a bootstrap credential.</summary>
    public TimeSpan BootstrapLifetime { get; }

    /// <summary>Maximum configured lifetime for a source-observation credential.</summary>
    public TimeSpan SourceLifetime { get; }
}
