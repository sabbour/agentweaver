using Agentweaver.Identity;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Environment;

public static class RemoteMcpTrustedServiceTlsComposition
{
    public const string IdentityTlsKey = "remote-mcp-credential-use-identity";
    public const string IdentitySectionName = "Environment:RemoteMcpCredentialUse:IdentityTls";
    public const string IdentityListenerName = "RemoteMcpCredentialUse";
    public const string IdentityClientName = "RemoteMcpCredentialUseIdentity";
    public const string OrchestratorTlsKey = "remote-mcp-call-verification-orchestrator";
    public const string OrchestratorSectionName = "Environment:RemoteMcpCallVerification:Tls";
    public const string OrchestratorClientName = "RemoteMcpCallVerificationOwner";

    public static void AddRemoteMcpTrustedServiceTls(this WebApplicationBuilder builder)
    {
        var identity = ReadProfile(builder, IdentitySectionName, "IdentityBroker");
        var orchestrator = ReadProfile(builder, OrchestratorSectionName, "Orchestrator");
        if (identity is not null && orchestrator is not null &&
            (Guid.Parse(identity.LocalServicePrincipalId!) != Guid.Parse(orchestrator.LocalServicePrincipalId!) ||
                Guid.Parse(identity.PeerServicePrincipalId!) == Guid.Parse(orchestrator.PeerServicePrincipalId!) ||
                identity.PeerCertificateSha256Pins!.Any(pin =>
                    orchestrator.PeerCertificateSha256Pins!.Contains(pin, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Remote MCP owner clients require separate, consistent service profiles.");
        if (identity is not null)
        {
            var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
            if (!Uri.TryCreate(endpoints[IdentityListenerName + ":Url"], UriKind.Absolute, out var address) ||
                !RuntimeContractValidation.IsHttpsEndpoint(address) || address.AbsolutePath != "/" ||
                address.Port != identity.ListenerPort ||
                endpoints.GetChildren().Any(endpoint =>
                    endpoint.Key != IdentityListenerName &&
                    Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var other) &&
                    other.Port == identity.ListenerPort))
                throw new InvalidOperationException("Identity verification requires its own Environment HTTPS listener and port.");
            AddClient(builder, IdentityTlsKey, IdentityClientName, identity, "IdentityBroker");
            builder.WebHost.ConfigureKestrel(server =>
            {
                var loader = server.ConfigurationLoader ?? server.Configure(builder.Configuration.GetSection("Kestrel"));
                loader.Endpoint(IdentityListenerName, endpoint =>
                {
                    var peer = server.ApplicationServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(IdentityTlsKey);
                    endpoint.HttpsOptions.ServerCertificate = peer.Certificate;
                    endpoint.HttpsOptions.SslProtocols = RuntimeServiceTlsIdentity.AllowedProtocols;
                    endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    endpoint.HttpsOptions.CheckCertificateRevocation = true;
                    endpoint.HttpsOptions.ClientCertificateValidation = peer.AcceptClientCertificate;
                });
            });
        }
        if (orchestrator is not null)
            AddClient(builder, OrchestratorTlsKey, OrchestratorClientName, orchestrator, "Orchestrator");
    }

    public static void RequireIdentityPeer(HttpContext context)
    {
        var options = context.RequestServices.GetKeyedService<RuntimeServiceTlsOptions>(IdentityTlsKey)
            ?? throw new RuntimeAuthorizationException("remote_mcp_current_verification_route_unavailable");
        if (options.LocalServiceName != "Environment" || options.PeerServiceName != "IdentityBroker")
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_peer_profile_invalid");
        var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
        RuntimeServiceTlsIdentity.RequireDirectTls(
            context.Request.IsHttps, protocol, context.Connection.LocalPort, options.ListenerPort);
        var identity = context.RequestServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(IdentityTlsKey);
        if (identity.LocalServiceName != "Environment" || identity.PeerServiceName != "IdentityBroker")
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_peer_profile_invalid");
        identity.RequirePeerConnection(
            context.Request.IsHttps, protocol, context.Connection.LocalPort,
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
    }

    public static void RequireOrchestratorPeer(HttpContext context)
    {
        var options = context.RequestServices.GetKeyedService<RuntimeServiceTlsOptions>(OrchestratorTlsKey)
            ?? throw new RuntimeAuthorizationException("remote_mcp_native_call_route_unavailable");
        if (options.LocalServiceName != "Environment" || options.PeerServiceName != "Orchestrator")
            throw new RuntimeAuthorizationException("remote_mcp_native_call_peer_profile_invalid");
        var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
        RuntimeServiceTlsIdentity.RequireDirectTls(
            context.Request.IsHttps, protocol, context.Connection.LocalPort, options.ListenerPort);
        var identity = context.RequestServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(OrchestratorTlsKey);
        if (identity.LocalServiceName != "Environment" || identity.PeerServiceName != "Orchestrator")
            throw new RuntimeAuthorizationException("remote_mcp_native_call_peer_profile_invalid");
        identity.RequirePeerConnection(
            context.Request.IsHttps, protocol, context.Connection.LocalPort,
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
    }

    private static RuntimeServiceTlsOptions? ReadProfile(
        WebApplicationBuilder builder, string sectionName, string peerName)
    {
        var section = builder.Configuration.GetSection(sectionName);
        if (!section.Exists())
            return null;
        var options = (section.Get<RuntimeServiceTlsOptions>()
            ?? throw new InvalidOperationException("An explicit Remote MCP owner TLS profile is required."))
            .Validate("Environment", peerName);
        if (options.PeerCertificateSha256Pins!.Contains(
                options.LocalCertificateSha256!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Remote MCP owner requires a different service certificate.");
        return options;
    }

    private static void AddClient(
        WebApplicationBuilder builder, string key, string clientName, RuntimeServiceTlsOptions options, string peerName)
    {
        builder.Services.AddKeyedSingleton(key, options);
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(key, (services, _) =>
            new RuntimeServiceTlsIdentity(options, "Environment", peerName, services.GetRequiredService<TimeProvider>()));
        builder.Services.AddHttpClient(clientName, client =>
        {
            client.BaseAddress = new Uri(options.PeerOwnerAddress!);
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(services =>
        {
            var handler = services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key).CreateHandler();
            handler.UseCookies = false;
            return handler;
        });
    }
}
