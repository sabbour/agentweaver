using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Builds the client-side mTLS <see cref="HttpClientHandler"/> used by every HttpClient that
/// calls a per-run AgentHost pod (readiness probe, A2A streaming, preview-runner, approvals — all
/// share the single named client <c>a2a-sandbox-pod</c> / <c>a2a-sandbox-pod-streaming</c>).
///
/// <para>
/// This is the wiring referenced by the comment in <c>Program.cs</c> ("attach the client
/// certificate handler here ... left as the documented hook") that was never implemented: with
/// <see cref="SandboxAgentOptions.RequireMtls"/> true, the AgentHost pod's Kestrel A2A listener
/// binds HTTPS-only (<see cref="Agentweaver.AgentHost.AgentHostKestrelConfigurator"/>) and
/// requires a client certificate. Without this handler the worker/API present no client cert
/// (server-side rejects the handshake) and, separately, .NET's default server-certificate
/// validation rejects AgentHost's self-signed/private-CA-issued server cert outright (untrusted
/// chain) — both failures manifest at the readiness probe as
/// <c>AuthenticationException: RemoteCertificateNameMismatch, RemoteCertificateChainErrors</c>.
/// </para>
///
/// <para>
/// AgentHost pods are ephemeral and per-run: they get a fresh pod IP every launch, so the server
/// certificate cannot enumerate every possible IP in its SAN list. Hostname/IP validation is
/// therefore deliberately skipped — but chain-of-trust validation against the pinned CA is NOT:
/// this mirrors <see cref="Agentweaver.AgentHost.AgentHostKestrelConfigurator.ValidateClientCertificate"/>,
/// which does the same thing in the opposite direction for the client certificate AgentHost
/// receives.
/// </para>
/// </summary>
internal static class AgentHostMtlsClientHandler
{
    /// <summary>
    /// Builds the primary message handler for the AgentHost-facing named HttpClients. Returns a
    /// handler with no client certificate and default validation when
    /// <see cref="SandboxAgentOptions.RequireMtls"/> is <see langword="false"/> (PoC plain-http
    /// mode) — in that mode there is no TLS to configure.
    /// </summary>
    public static HttpClientHandler Create(SandboxAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new HttpClientHandler();

        if (!options.RequireMtls)
            return handler;

        var clientCertificate = X509Certificate2.CreateFromPemFile(
            options.ClientCertificatePath, options.ClientCertificateKeyPath);
        var certificateAuthority = X509Certificate2.CreateFromPem(
            File.ReadAllText(options.ClientCACertPath));

        handler.ClientCertificates.Add(clientCertificate);
        // ClientCertificateOptions.Manual (default) + the certs above is sufficient; no automatic
        // OS-cert-store lookup is needed since we present exactly one cert we loaded ourselves.
        handler.ServerCertificateCustomValidationCallback =
            (_, certificate, _, sslPolicyErrors) =>
                ValidateServerCertificate(certificate, sslPolicyErrors, certificateAuthority);

        return handler;
    }

    /// <summary>
    /// Validates the AgentHost pod's server certificate against the pinned CA, ignoring ONLY a
    /// hostname/IP mismatch (expected — pod IPs are ephemeral and not enumerable in advance).
    /// Any other policy error (chain errors NOT explained by trusting our own CA, or a missing
    /// certificate) still fails the handshake.
    /// </summary>
    internal static bool ValidateServerCertificate(
        X509Certificate2? certificate,
        SslPolicyErrors sslPolicyErrors,
        X509Certificate2 certificateAuthority)
    {
        if (certificate is null)
            return false;

        // A mismatch is the only error we tolerate on its own; anything else needs the chain
        // re-validated against our pinned CA below (the OS chain will legitimately report
        // RemoteCertificateChainErrors for a private-CA-issued leaf, which is expected here).
        var unexpectedErrors = sslPolicyErrors & ~(SslPolicyErrors.RemoteCertificateNameMismatch
            | SslPolicyErrors.RemoteCertificateChainErrors);
        if (unexpectedErrors != SslPolicyErrors.None)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);
        chain.ChainPolicy.ExtraStore.Add(certificateAuthority);
        return chain.Build(certificate);
    }
}
