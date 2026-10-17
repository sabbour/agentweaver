using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Environment;

/// <summary>
/// Configures certificate-pinned mutual TLS for trusted Remote MCP owner calls and their private listeners.
/// </summary>
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
    public const string CurrentCallVerifierEnabledSetting =
        "Environment:RemoteMcpCallVerification:EnableCurrentCallVerifier";
    public const string ManagedGatewaySectionName = "Environment:NetworkPolicy:RemoteMcpManagedGateway";

    /// <summary>Loads the explicit service TLS profiles and configures their outbound clients and dedicated listeners.</summary>
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

    /// <summary>Requires the current request to arrive from the configured Identity service peer.</summary>
    public static void RequireIdentityPeer(HttpContext context) =>
        RequirePeer(context, IdentityTlsKey, "IdentityBroker",
            "remote_mcp_current_verification_route_unavailable",
            "remote_mcp_current_verification_peer_profile_invalid");

    /// <summary>Enables dispatch only when the reviewed recipient and current-call verifier are fully registered.</summary>
    public static bool ConfigureRemoteMcpNativeCallDispatch(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>(OrchestratorRecipientEnabledSetting))
            return false;
        if (!builder.Configuration.GetValue<bool>(CurrentCallVerifierEnabledSetting) ||
            !builder.Services.Any(service => service.ServiceType == typeof(RuntimeServiceTlsOptions) &&
                Equals(service.ServiceKey, OrchestratorRecipientTlsKey)) ||
            !builder.Services.Any(service =>
                service.ServiceType == typeof(RemoteMcpCurrentCallVerificationService)))
            throw new InvalidOperationException(
                "Reviewed-tool dispatch requires its private Orchestrator recipient and current-call verifier.");
        return true;
    }

    /// <summary>Registers current-call verification only when both trusted owner TLS profiles are available.</summary>
    public static bool AddRemoteMcpCurrentCallVerification(this WebApplicationBuilder builder, string actorIssuer)
    {
        if (!builder.Configuration.GetValue<bool>(CurrentCallVerifierEnabledSetting))
            return false;
        if (!builder.Services.Any(service => service.ServiceType == typeof(RuntimeServiceTlsOptions) &&
                Equals(service.ServiceKey, IdentityTlsKey)) ||
            !builder.Services.Any(service => service.ServiceType == typeof(RuntimeServiceTlsOptions) &&
                Equals(service.ServiceKey, OrchestratorTlsKey)))
            throw new InvalidOperationException("Current-call verification requires both trusted owner TLS profiles.");
        var verification = new RemoteMcpCurrentCallVerificationOptions(actorIssuer).Validate();
        var gateway = (builder.Configuration.GetSection(ManagedGatewaySectionName)
            .Get<RemoteMcpManagedGatewayProviderOptions>()
            ?? throw new InvalidOperationException("Current-call verification requires explicit managed gateway options."))
            .Validate();
        builder.Services.AddSingleton(verification);
        builder.Services.AddSingleton(gateway);
        builder.Services.AddSingleton<RemoteMcpManagedGatewayPolicy>();
        builder.Services.AddSingleton<IRemoteMcpManagedGatewayPolicy>(services =>
            services.GetRequiredService<RemoteMcpManagedGatewayPolicy>());
        builder.Services.AddSingleton<IRemoteMcpManagedGatewayStateReader>(services =>
            services.GetRequiredService<RemoteMcpManagedGatewayPolicy>());
        builder.Services.AddScoped<RemoteMcpCurrentGatewayPolicyReader>();
        builder.Services.AddScoped<RemoteMcpCurrentCallVerificationService>();
        return true;
    }

    /// <summary>Requires the current request to arrive from the configured Orchestrator service peer.</summary>
    public static void RequireOrchestratorPeer(HttpContext context) =>
        RequirePeer(context, OrchestratorRecipientTlsKey, "Orchestrator",
            "remote_mcp_reviewed_tool_recipient_unavailable",
            "remote_mcp_reviewed_tool_recipient_profile_invalid");

    /// <summary>Validates the peer profile, direct TLS listener, and client certificate for the current request.</summary>
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

    /// <summary>Ensures the private service listener has its own HTTPS endpoint and port.</summary>
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

    /// <summary>Configures a dedicated Kestrel listener to require and validate the pinned client certificate.</summary>
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

    /// <summary>Reads and validates one optional local/peer identity profile, rejecting reused service certificates.</summary>
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

    /// <summary>Registers a certificate-pinned mutual-TLS HTTP client for one trusted service owner.</summary>
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
