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
    public const string OrchestratorRecipientEnabledSetting =
        "Environment:RemoteMcpCallVerification:EnableReviewedToolRecipient";
    public const string OrchestratorRecipientTlsKey = "remote-mcp-reviewed-tool-orchestrator-recipient";
    public const string OrchestratorRecipientListenerName = "RemoteMcpReviewedToolTransport";

    public static void AddRemoteMcpTrustedServiceTls(this WebApplicationBuilder builder)
    {
        var identity = ReadProfile(builder, IdentitySectionName, "IdentityBroker");
        var orchestrator = ReadProfile(builder, OrchestratorSectionName, "Orchestrator");
        var enableOrchestratorRecipient = builder.Configuration.GetValue<bool>(OrchestratorRecipientEnabledSetting);
        if (identity is not null && orchestrator is not null &&
            (Guid.Parse(identity.LocalServicePrincipalId!) != Guid.Parse(orchestrator.LocalServicePrincipalId!) ||
                Guid.Parse(identity.PeerServicePrincipalId!) == Guid.Parse(orchestrator.PeerServicePrincipalId!) ||
                identity.PeerCertificateSha256Pins!.Any(pin =>
                    orchestrator.PeerCertificateSha256Pins!.Contains(pin, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Remote MCP owner clients require separate, consistent service profiles.");
        if (enableOrchestratorRecipient)
        {
            if (orchestrator is null)
                throw new InvalidOperationException("The reviewed-tool recipient requires an explicit Orchestrator TLS profile.");
            RequireDedicatedListener(builder, OrchestratorRecipientListenerName, orchestrator.ListenerPort,
                "Reviewed-tool requests require a separate Orchestrator recipient HTTPS listener and port.");
        }
        if (identity is not null)
        {
            RequireDedicatedListener(builder, IdentityListenerName, identity.ListenerPort,
                "Identity verification requires its own Environment HTTPS listener and port.");
            AddClient(builder, IdentityTlsKey, IdentityClientName, identity, "IdentityBroker");
            ConfigureListener(builder, IdentityListenerName, IdentityTlsKey);
        }
        if (orchestrator is not null)
        {
            AddClient(builder, OrchestratorTlsKey, OrchestratorClientName, orchestrator, "Orchestrator");
            if (enableOrchestratorRecipient)
            {
                builder.Services.AddKeyedSingleton(OrchestratorRecipientTlsKey, orchestrator);
                builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(OrchestratorRecipientTlsKey, (services, _) =>
                    services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(OrchestratorTlsKey));
                ConfigureListener(builder, OrchestratorRecipientListenerName, OrchestratorRecipientTlsKey);
            }
        }
    }

    public static void RequireIdentityPeer(HttpContext context) =>
        RequirePeer(context, IdentityTlsKey, "IdentityBroker",
            "remote_mcp_current_verification_route_unavailable",
            "remote_mcp_current_verification_peer_profile_invalid");

    public static void RequireOrchestratorPeer(HttpContext context) =>
        RequirePeer(context, OrchestratorRecipientTlsKey, "Orchestrator",
            "remote_mcp_reviewed_tool_recipient_unavailable",
            "remote_mcp_reviewed_tool_recipient_profile_invalid");

    private static void RequirePeer(
        HttpContext context, string key, string peerName, string unavailableCode, string profileCode)
    {
        var options = context.RequestServices.GetKeyedService<RuntimeServiceTlsOptions>(key)
            ?? throw new RuntimeAuthorizationException(unavailableCode);
        if (options.LocalServiceName != "Environment" || options.PeerServiceName != peerName)
            throw new RuntimeAuthorizationException(profileCode);
        var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
        RuntimeServiceTlsIdentity.RequireDirectTls(
            context.Request.IsHttps, protocol, context.Connection.LocalPort, options.ListenerPort);
        var identity = context.RequestServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key);
        if (identity.LocalServiceName != "Environment" || identity.PeerServiceName != peerName)
            throw new RuntimeAuthorizationException(profileCode);
        identity.RequirePeerConnection(
            context.Request.IsHttps, protocol, context.Connection.LocalPort,
            context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
    }

    private static void RequireDedicatedListener(
        WebApplicationBuilder builder, string listenerName, int port, string error)
    {
        var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
        if (!Uri.TryCreate(endpoints[listenerName + ":Url"], UriKind.Absolute, out var address) ||
            !RuntimeContractValidation.IsHttpsEndpoint(address) || address.AbsolutePath != "/" ||
            address.Port != port ||
            endpoints.GetChildren().Any(endpoint =>
                endpoint.Key != listenerName &&
                Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var other) && other.Port == port))
            throw new InvalidOperationException(error);
    }

    private static void ConfigureListener(WebApplicationBuilder builder, string listenerName, string key)
    {
        builder.WebHost.ConfigureKestrel(server =>
        {
            var loader = server.ConfigurationLoader ?? server.Configure(builder.Configuration.GetSection("Kestrel"));
            loader.Endpoint(listenerName, endpoint =>
            {
                var peer = server.ApplicationServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key);
                endpoint.HttpsOptions.ServerCertificate = peer.Certificate;
                endpoint.HttpsOptions.SslProtocols = RuntimeServiceTlsIdentity.AllowedProtocols;
                endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                endpoint.HttpsOptions.CheckCertificateRevocation = true;
                endpoint.HttpsOptions.ClientCertificateValidation = peer.AcceptClientCertificate;
            });
        });
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
            services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(key).CreateHandler());
    }
}
