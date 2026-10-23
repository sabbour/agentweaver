using Agentweaver.Identity;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Identity.Broker;

/// <summary>Configures the dedicated AgentHost channel used to redeem hosted model credentials.</summary>
public static class HostedModelCatalogCredentialUseComposition
{
    public const string RecipientTlsKey = "hosted-model-catalog-credential-use-recipient";
    public const string SectionName = "IdentityBroker:HostedModelCatalogCredentialUse";
    public const string ListenerName = "HostedModelCatalogCredentialUse";

    /// <summary>Adds the pinned AgentHost TLS profile and isolated HTTPS listener when configured.</summary>
    public static bool AddHostedModelCatalogCredentialUseTls(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(SectionName);
        if (!section.Exists())
            return false;
        var options = (section.GetSection("Tls").Get<RuntimeServiceTlsOptions>()
            ?? throw new InvalidOperationException("Hosted model credential use requires an explicit TLS profile."))
            .Validate("IdentityBroker", "AgentHost");
        if (options.PeerCertificateSha256Pins!.Contains(
                options.LocalCertificateSha256!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The AgentHost recipient requires a different service certificate.");
        var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
        // A separate port keeps the mTLS identity bound to this one credential-use route.
        if (!Uri.TryCreate(endpoints[ListenerName + ":Url"], UriKind.Absolute, out var address) ||
            !RuntimeContractValidation.IsHttpsEndpoint(address) || address.AbsolutePath != "/" ||
            address.Port != options.ListenerPort ||
            endpoints.GetChildren().Any(endpoint =>
                endpoint.Key != ListenerName &&
                Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var other) &&
                other.Port == options.ListenerPort))
            throw new InvalidOperationException(
                "Hosted model credential use requires its own configured HTTPS listener and port.");

        builder.Services.AddKeyedSingleton(RecipientTlsKey, options);
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(RecipientTlsKey, (services, _) =>
            new RuntimeServiceTlsIdentity(
                options, "IdentityBroker", "AgentHost", services.GetRequiredService<TimeProvider>()));
        // The shared loader preserves the Issue Sync and Remote MCP endpoint callbacks.
        KestrelEndpointComposition.AddEndpoint(builder, (loader, server) =>
            loader.Endpoint(ListenerName, endpoint =>
            {
                var identity = server.ApplicationServices
                    .GetRequiredKeyedService<RuntimeServiceTlsIdentity>(RecipientTlsKey);
                endpoint.HttpsOptions.ServerCertificate = identity.Certificate;
                endpoint.HttpsOptions.SslProtocols = RuntimeServiceTlsIdentity.AllowedProtocols;
                endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                endpoint.HttpsOptions.CheckCertificateRevocation = true;
                endpoint.HttpsOptions.ClientCertificateValidation = identity.AcceptClientCertificate;
            }));
        return true;
    }

    /// <summary>Requires the dedicated listener and the pinned AgentHost client certificate.</summary>
    public static void RequireAgentHost(HttpContext context)
    {
        var options = context.RequestServices
            .GetKeyedService<RuntimeServiceTlsOptions>(RecipientTlsKey)
            ?? throw new RuntimeAuthorizationException("hosted_model_catalog_credential_use_unavailable");
        if (options.LocalServiceName != "IdentityBroker" || options.PeerServiceName != "AgentHost")
            throw new RuntimeAuthorizationException("hosted_model_catalog_credential_use_recipient_profile_invalid");
        var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
        // The bearer authorizes the user; only the pinned TLS peer authorizes credential redemption.
        RuntimeServiceTlsIdentity.RequireDirectTls(
            context.Request.IsHttps, protocol, context.Connection.LocalPort, options.ListenerPort);
        var identity = context.RequestServices
            .GetRequiredKeyedService<RuntimeServiceTlsIdentity>(RecipientTlsKey);
        if (identity.LocalServiceName != "IdentityBroker" || identity.PeerServiceName != "AgentHost")
            throw new RuntimeAuthorizationException("hosted_model_catalog_credential_use_recipient_profile_invalid");
        identity.RequirePeerConnection(
            context.Request.IsHttps, protocol, context.Connection.LocalPort,
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
    }
}
