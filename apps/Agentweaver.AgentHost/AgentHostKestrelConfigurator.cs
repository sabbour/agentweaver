using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.AgentHost;

internal sealed record AgentHostKestrelBindingPlan(
    bool RequireMtls,
    int A2APort,
    bool KestrelEndpointsConfigured,
    bool BindPlainHttpFallback,
    bool BindMtlsFallback,
    string ServerCertificatePath,
    string ServerCertificateKeyPath,
    string ClientCACertificatePath);

internal static class AgentHostKestrelConfigurator
{
    public static AgentHostKestrelBindingPlan Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection("AgentHost").Get<AgentHostOptions>() ?? new AgentHostOptions();
        var kestrelEndpointsConfigured = configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();

        return new AgentHostKestrelBindingPlan(
            options.RequireMtls,
            options.Port,
            kestrelEndpointsConfigured,
            BindPlainHttpFallback: !options.RequireMtls && !kestrelEndpointsConfigured,
            BindMtlsFallback: options.RequireMtls && !kestrelEndpointsConfigured,
            options.ServerCertificatePath,
            options.ServerCertificateKeyPath,
            options.ClientCACertPath);
    }

    public static void Configure(KestrelServerOptions kestrel, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(configuration);

        var plan = Resolve(configuration);

        if (plan.KestrelEndpointsConfigured)
            kestrel.Configure(configuration.GetSection("Kestrel"));

        if (plan.RequireMtls)
        {
            kestrel.ConfigureHttpsDefaults(httpsOptions => ApplyMtlsDefaults(httpsOptions, plan));
        }

        if (plan.BindPlainHttpFallback)
        {
            kestrel.Listen(IPAddress.Any, plan.A2APort);
            return;
        }

        if (plan.BindMtlsFallback)
        {
            kestrel.Listen(IPAddress.Any, plan.A2APort, listenOptions =>
                listenOptions.UseHttps(httpsOptions => ApplyMtlsEndpoint(httpsOptions, plan)));
        }
    }

    internal static void ApplyMtlsDefaults(
        HttpsConnectionAdapterOptions httpsOptions,
        AgentHostKestrelBindingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(httpsOptions);
        ArgumentNullException.ThrowIfNull(plan);

        var certificateAuthority = LoadPublicCertificate(plan.ClientCACertificatePath);
        httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        httpsOptions.ClientCertificateValidation = (certificate, _, _) =>
            ValidateClientCertificate(certificate, certificateAuthority);
    }

    internal static void ApplyMtlsEndpoint(
        HttpsConnectionAdapterOptions httpsOptions,
        AgentHostKestrelBindingPlan plan)
    {
        ApplyMtlsDefaults(httpsOptions, plan);
        httpsOptions.ServerCertificate = LoadPemCertificate(
            plan.ServerCertificatePath,
            plan.ServerCertificateKeyPath);
    }

    internal static bool ValidateClientCertificate(
        X509Certificate2? clientCertificate,
        X509Certificate2 certificateAuthority)
    {
        if (clientCertificate is null)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);
        chain.ChainPolicy.ExtraStore.Add(certificateAuthority);
        return chain.Build(clientCertificate);
    }

    internal static X509Certificate2 LoadPemCertificate(string certificatePath, string keyPath)
    {
        return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
    }

    internal static X509Certificate2 LoadPublicCertificate(string certificatePath)
    {
        return X509Certificate2.CreateFromPemFile(certificatePath);
    }
}
