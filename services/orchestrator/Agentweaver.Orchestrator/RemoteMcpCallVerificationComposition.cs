using Agentweaver.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Orchestrator;

public static class RemoteMcpCallVerificationComposition
{
    public const string TlsKey = "remote-mcp-call-verification-environment";
    public const string SectionName = "Orchestrator:RemoteMcpCallVerification";
    public const string ListenerName = "RemoteMcpCallVerification";
    public const string ReviewedToolTransportEnabledSetting = SectionName + ":EnableReviewedToolTransport";

    public static bool AddReviewedRemoteToolTransport(
        this WebApplicationBuilder builder, bool runtimeRegistrationEnabled)
    {
        if (!builder.Configuration.GetValue<bool>(ReviewedToolTransportEnabledSetting))
            return false;
        if (!runtimeRegistrationEnabled ||
            !builder.Services.Any(service => service.ServiceType == typeof(RuntimeServiceTlsOptions) &&
                Equals(service.ServiceKey, TlsKey)))
            throw new InvalidOperationException(
                "Reviewed-tool transport requires the runtime registration owner and Environment TLS profile.");
        builder.Services.AddHttpClient<ReviewedRemoteToolTransportClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(services =>
            {
                var handler = services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(TlsKey).CreateHandler();
                handler.UseCookies = false;
                return handler;
            });
        builder.Services.AddScoped<IReviewedRemoteToolTransport>(services =>
            services.GetRequiredService<ReviewedRemoteToolTransportClient>());
        return true;
    }

    public static bool AddRemoteMcpCallVerificationTls(
        this WebApplicationBuilder builder, bool runtimeRegistrationEnabled)
    {
        var section = builder.Configuration.GetSection(SectionName);
        if (!section.Exists())
            return false;
        if (!runtimeRegistrationEnabled)
            throw new InvalidOperationException("Remote MCP call verification requires the runtime registration owner.");
        var options = (section.GetSection("Tls").Get<RuntimeServiceTlsOptions>()
            ?? throw new InvalidOperationException("Remote MCP call verification requires an explicit TLS profile."))
            .Validate("Orchestrator", "Environment");
        if (options.PeerCertificateSha256Pins!.Contains(
                options.LocalCertificateSha256!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Environment caller requires a different service certificate.");
        var eventsSection = builder.Configuration.GetSection("Orchestrator:RemoteToolResults:Tls");
        if (eventsSection.Exists())
        {
            var events = (eventsSection.Get<RuntimeServiceTlsOptions>()
                ?? throw new InvalidOperationException("The existing Events TLS profile is invalid."))
                .Validate("Orchestrator", "EventsAndSessions");
            if (Guid.Parse(events.LocalServicePrincipalId!) != Guid.Parse(options.LocalServicePrincipalId!) ||
                Guid.Parse(events.PeerServicePrincipalId!) == Guid.Parse(options.PeerServicePrincipalId!) ||
                options.PeerCertificateSha256Pins!.Any(pin =>
                    events.PeerCertificateSha256Pins!.Contains(pin, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Environment and Events require separate, consistent service profiles.");
        }
        var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
        if (!Uri.TryCreate(endpoints[ListenerName + ":Url"], UriKind.Absolute, out var address) ||
            !RuntimeContractValidation.IsHttpsEndpoint(address) || address.AbsolutePath != "/" ||
            address.Port != options.ListenerPort ||
            endpoints.GetChildren().Any(endpoint =>
                endpoint.Key != ListenerName &&
                Uri.TryCreate(endpoint["Url"], UriKind.Absolute, out var other) &&
                other.Port == options.ListenerPort))
            throw new InvalidOperationException("Remote MCP call verification requires its own HTTPS listener and port.");

        builder.Services.AddKeyedSingleton(TlsKey, options);
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(TlsKey, (services, _) =>
            new RuntimeServiceTlsIdentity(
                options, "Orchestrator", "Environment", services.GetRequiredService<TimeProvider>()));
        builder.WebHost.ConfigureKestrel(server =>
        {
            var loader = server.ConfigurationLoader ?? server.Configure(builder.Configuration.GetSection("Kestrel"));
            loader.Endpoint(ListenerName, endpoint =>
            {
                var identity = server.ApplicationServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(TlsKey);
                endpoint.HttpsOptions.ServerCertificate = identity.Certificate;
                endpoint.HttpsOptions.SslProtocols = RuntimeServiceTlsIdentity.AllowedProtocols;
                endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                endpoint.HttpsOptions.CheckCertificateRevocation = true;
                endpoint.HttpsOptions.ClientCertificateValidation = identity.AcceptClientCertificate;
            });
        });
        return true;
    }
}
