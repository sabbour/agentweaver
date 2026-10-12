using Agentweaver.Identity;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Identity.Broker;

public static class RemoteMcpCredentialUseComposition
{
    public const string RecipientTlsKey = "remote-mcp-credential-use-recipient";
    public const string SectionName = "IdentityBroker:RemoteMcpCredentialUse";
    public const string ListenerName = "RemoteMcpCredentialUse";

    public static bool AddRemoteMcpCredentialUseTls(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(SectionName);
        if (!section.Exists())
            return false;
        var options = (section.GetSection("Tls").Get<RuntimeServiceTlsOptions>()
            ?? throw new InvalidOperationException("Remote MCP credential use requires an explicit TLS profile."))
            .Validate("IdentityBroker", "Environment");
        if (options.PeerCertificateSha256Pins!.Contains(
                options.LocalCertificateSha256!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Environment recipient requires a different service certificate.");
        var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
        if (!Uri.TryCreate(endpoints[ListenerName + ":Url"], UriKind.Absolute, out var address) ||
            !RuntimeContractValidation.IsHttpsEndpoint(address) || address.AbsolutePath != "/" ||
            address.Port != options.ListenerPort ||
            endpoints.GetChildren().Any(endpoint =>
                endpoint.Key != ListenerName &&
                Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var other) &&
                other.Port == options.ListenerPort))
            throw new InvalidOperationException(
                "Remote MCP credential use requires its own configured HTTPS listener and port.");

        builder.Services.AddKeyedSingleton(RecipientTlsKey, options);
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(RecipientTlsKey, (services, _) =>
            new RuntimeServiceTlsIdentity(
                options, "IdentityBroker", "Environment", services.GetRequiredService<TimeProvider>()));
        builder.WebHost.ConfigureKestrel(server =>
            server.Configure(builder.Configuration.GetSection("Kestrel")).Endpoint(ListenerName, endpoint =>
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

    public static void RequireRecipient(HttpContext context)
    {
        var options = context.RequestServices
            .GetKeyedService<RuntimeServiceTlsOptions>(RecipientTlsKey)
            ?? throw new RuntimeAuthorizationException("remote_mcp_credential_use_route_unavailable");
        if (options.LocalServiceName != "IdentityBroker" || options.PeerServiceName != "Environment")
            throw new RuntimeAuthorizationException("remote_mcp_credential_use_recipient_profile_invalid");
        var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
        RuntimeServiceTlsIdentity.RequireDirectTls(
            context.Request.IsHttps, protocol, context.Connection.LocalPort, options.ListenerPort);
        var identity = context.RequestServices
            .GetRequiredKeyedService<RuntimeServiceTlsIdentity>(RecipientTlsKey);
        if (identity.LocalServiceName != "IdentityBroker" || identity.PeerServiceName != "Environment")
            throw new RuntimeAuthorizationException("remote_mcp_credential_use_recipient_profile_invalid");
        identity.RequirePeerConnection(
            context.Request.IsHttps, protocol, context.Connection.LocalPort,
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
    }
}
