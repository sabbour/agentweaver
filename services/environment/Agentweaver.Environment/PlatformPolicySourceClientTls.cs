using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Identity;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Environment;

internal static class PlatformPolicySourceClientTls
{
    internal const string SourceSectionName = "Environment:PlatformEgress:Source";
    internal const string TlsSectionName = SourceSectionName + ":Tls";
    internal const string RoutePath = "/internal/policy-source/platform";
    internal const string EnvironmentDnsName = "environment.agentweaver.svc.cluster.local";
    internal const string DefaultBackendCaPath = "/var/run/service-backend-ca/ca.crt";

    internal static readonly string[] RemoteMcpProfileSections =
    [
        RemoteMcpTrustedServiceTlsComposition.IdentitySectionName,
        RemoteMcpTrustedServiceTlsComposition.OrchestratorSectionName,
    ];

    internal static bool IsEnabled(IConfiguration configuration)
    {
        var value = configuration[$"{SourceSectionName}:Enabled"];
        if (value is not null && !bool.TryParse(value, out _))
            throw InvalidConfiguration();
        return bool.TryParse(value, out var enabled) && enabled;
    }

    internal static PlatformPolicySourceClientTlsIdentity CreateIdentity(
        IConfiguration configuration,
        Uri projectsBaseAddress,
        TimeProvider timeProvider)
    {
        if (projectsBaseAddress.Scheme != Uri.UriSchemeHttps ||
            projectsBaseAddress.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(projectsBaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(projectsBaseAddress.Query) ||
            !string.IsNullOrEmpty(projectsBaseAddress.Fragment)
        )
            throw InvalidConfiguration();

        var options = ReadOptions(configuration.GetSection(TlsSectionName), timeProvider.GetUtcNow());
        var identity = new PlatformPolicySourceClientTlsIdentity(
            options, configuration, timeProvider);
        identity.ValidateConfiguration();
        return identity;
    }

    private static PlatformPolicySourceClientTlsOptions ReadOptions(
        IConfigurationSection section,
        DateTimeOffset now)
    {
        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ClientCertificatePemPath",
            "ClientPrivateKeyPemPath",
            "ClientCertificateSha256",
            "BackendCaPemPath",
            "BackendCaSha256",
            "ProjectsPins",
        };
        if (section.GetChildren().Any(child => !allowedKeys.Contains(child.Key)))
            throw InvalidConfiguration();

        var certificatePath = RequireAbsolutePath(section["ClientCertificatePemPath"]);
        var privateKeyPath = RequireAbsolutePath(section["ClientPrivateKeyPemPath"]);
        var clientCertificateSha256 = ReadDigest(section["ClientCertificateSha256"]);
        var backendCaPath = section["BackendCaPemPath"] is { } configuredCaPath
            ? RequireAbsolutePath(configuredCaPath)
            : DefaultBackendCaPath;
        var backendCaSha256 = ReadDigest(section["BackendCaSha256"]);
        var pinItems = section.GetSection("ProjectsPins").GetChildren()
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (pinItems.Length is < 1 or > 4)
            throw InvalidConfiguration();

        var pins = ImmutableArray.CreateBuilder<PlatformPolicySourcePeerPin>(pinItems.Length);
        for (var index = 0; index < pinItems.Length; index++)
        {
            var item = pinItems[index];
            if (item.Key != index.ToString(CultureInfo.InvariantCulture) ||
                item.GetChildren().Any(child => child.Key is not ("Sha256" or "RetireAtUtc")))
                throw InvalidConfiguration();

            var digest = ReadDigest(item["Sha256"]);
            var retirementValue = item["RetireAtUtc"];
            if (retirementValue is null ||
                !retirementValue.EndsWith('Z') ||
                !DateTimeOffset.TryParse(
                    retirementValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var retireAtUtc) ||
                retireAtUtc <= now)
                throw InvalidConfiguration();
            pins.Add(new PlatformPolicySourcePeerPin(digest, retireAtUtc));
        }

        if (pins.Select(pin => Convert.ToHexString(pin.Digest))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Count)
            throw InvalidConfiguration();

        return new PlatformPolicySourceClientTlsOptions(
            certificatePath,
            privateKeyPath,
            clientCertificateSha256,
            backendCaPath,
            backendCaSha256,
            pins.MoveToImmutable());
    }

    private static string RequireAbsolutePath(string? value) =>
        value is { Length: > 0 } path && Path.IsPathFullyQualified(path)
            ? path
            : throw InvalidConfiguration();

    private static byte[] ReadDigest(string? value)
    {
        if (value is not { Length: 64 } || !value.All(char.IsAsciiHexDigit))
            throw InvalidConfiguration();
        return Convert.FromHexString(value);
    }

    private static InvalidOperationException InvalidConfiguration() =>
        new("Environment:PlatformEgress:Source requires a complete, valid Projects TLS client profile.");
}

internal sealed record PlatformPolicySourcePeerPin(byte[] Digest, DateTimeOffset RetireAtUtc);

internal sealed record PlatformPolicySourceClientTlsOptions(
    string ClientCertificatePemPath,
    string ClientPrivateKeyPemPath,
    byte[] ClientCertificateSha256,
    string BackendCaPemPath,
    byte[] BackendCaSha256,
    ImmutableArray<PlatformPolicySourcePeerPin> ProjectsPins);

internal interface IPlatformPolicySourceHandlerLifetime
{
    Task ValidationCompleted { get; }
}

internal sealed class PlatformPolicySourceClientTlsIdentity(
    PlatformPolicySourceClientTlsOptions options,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan UrlRetrievalTimeout = TimeSpan.FromSeconds(5);

    internal void ValidateConfiguration()
    {
        var now = timeProvider.GetUtcNow();
        using var clientCertificate = LoadClientCertificate(now);
        using var backendCa = LoadBackendCa();
        EnsureProjectsPinsActive(now);
        ValidateSeparation(clientCertificate, now);
    }

    internal HttpMessageHandler CreateHandler()
    {
        var now = timeProvider.GetUtcNow();
        var clientCertificate = LoadClientCertificate(now);
        var tlsClientCertificate = clientCertificate;
        X509Certificate2? backendCa = null;
        try
        {
            tlsClientCertificate = PrepareClientCertificateForTls(clientCertificate);
            if (!ReferenceEquals(clientCertificate, tlsClientCertificate))
                clientCertificate.Dispose();
            backendCa = LoadBackendCa();
            EnsureProjectsPinsActive(now);
            ValidateSeparation(tlsClientCertificate, now);

            var validationLifetime = new ValidationCallbackLifetime(() =>
            {
                tlsClientCertificate.Dispose();
                backendCa.Dispose();
            });
            var preliminaryPolicy = new X509ChainPolicy
            {
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true,
                VerificationFlags = X509VerificationFlags.NoFlag,
            };
            var sslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = preliminaryPolicy,
                ClientCertificates = new X509CertificateCollection { tlsClientCertificate },
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    if (!validationLifetime.TryEnter())
                        return false;
                    try
                    {
                        return ValidateProjectsCertificate(certificate, chain, errors, backendCa);
                    }
                    finally
                    {
                        validationLifetime.Exit();
                    }
                },
            };
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.Zero,
                PooledConnectionIdleTimeout = TimeSpan.Zero,
                MaxConnectionsPerServer = 1,
                SslOptions = sslOptions,
            };
            return new OwnedHandler(handler, validationLifetime);
        }
        catch
        {
            if (!ReferenceEquals(clientCertificate, tlsClientCertificate))
                tlsClientCertificate.Dispose();
            clientCertificate.Dispose();
            backendCa?.Dispose();
            throw;
        }
    }

    private static X509Certificate2 PrepareClientCertificateForTls(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
            return certificate;

        var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password: null,
                X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private X509Certificate2 LoadClientCertificate(DateTimeOffset now)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPemFile(
                options.ClientCertificatePemPath,
                options.ClientPrivateKeyPemPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The Environment policy source client certificate is unavailable or invalid.", exception);
        }

        if (!certificate.HasPrivateKey ||
            certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                .Any(extension => extension.CertificateAuthority) ||
            !HasPurpose(certificate, ClientAuthenticationOid) ||
            !IsCurrentlyValid(certificate, now) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(certificate.RawData), options.ClientCertificateSha256) ||
            !HasExactEnvironmentSan(certificate))
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The Environment policy source client certificate does not match its TLS profile.");
        }
        return certificate;
    }

    private X509Certificate2 LoadBackendCa()
    {
        if (!File.Exists(options.BackendCaPemPath))
            throw new InvalidOperationException("The mounted backend CA is unavailable.");
        var root = X509CertificateLoader.LoadCertificateFromFile(options.BackendCaPemPath);
        if (!root.Extensions.OfType<X509BasicConstraintsExtension>()
                .Any(extension => extension.CertificateAuthority) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(root.RawData), options.BackendCaSha256))
        {
            root.Dispose();
            throw new InvalidOperationException(
                "The mounted backend CA does not match its TLS profile.");
        }
        return root;
    }

    private void ValidateSeparation(X509Certificate2 clientCertificate, DateTimeOffset now)
    {
        var clientDigest = SHA256.HashData(clientCertificate.RawData);
        if (options.ProjectsPins.Any(pin =>
                CryptographicOperations.FixedTimeEquals(pin.Digest, clientDigest)))
            throw new InvalidOperationException(
                "The Environment policy source client certificate must differ from every Projects pin.");

        foreach (var sectionName in PlatformPolicySourceClientTls.RemoteMcpProfileSections)
        {
            var section = configuration.GetSection(sectionName);
            if (!section.Exists())
                continue;

            var peerName = sectionName == RemoteMcpTrustedServiceTlsComposition.IdentitySectionName
                ? "IdentityBroker"
                : "Orchestrator";
            var profile = (section.Get<RuntimeServiceTlsOptions>()
                ?? throw new InvalidOperationException(
                    "A configured Remote MCP owner TLS profile is invalid."))
                .Validate("Environment", peerName);
            using var remoteIdentity = new RuntimeServiceTlsIdentity(
                profile, "Environment", peerName, timeProvider);
            if (CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(remoteIdentity.Certificate.RawData), clientDigest))
                throw new InvalidOperationException(
                    "The Environment policy source client must use a certificate distinct from Remote MCP owner identities.");
        }

        EnsureProjectsPinsActive(now);
    }

    private void EnsureProjectsPinsActive(DateTimeOffset now)
    {
        if (options.ProjectsPins.IsDefaultOrEmpty ||
            options.ProjectsPins.All(pin => now >= pin.RetireAtUtc))
            throw new InvalidOperationException(
                "The Environment policy source has no active Projects peer pin.");
    }

    private bool ValidateProjectsCertificate(
        X509Certificate? certificate,
        X509Chain? peerChain,
        SslPolicyErrors errors,
        X509Certificate2 backendCa)
    {
        var unexpectedErrors = errors & ~SslPolicyErrors.RemoteCertificateChainErrors;
        if (certificate is null || unexpectedErrors != SslPolicyErrors.None)
            return false;

        using var converted = certificate is X509Certificate2
            ? null
            : X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var leaf = certificate as X509Certificate2 ?? converted!;
        var now = timeProvider.GetUtcNow();
        var digest = SHA256.HashData(leaf.RawData);
        var caLeaf = leaf.Extensions.OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority);
        var serverPurpose = HasPurpose(leaf, ServerAuthenticationOid);
        var currentlyValid = IsCurrentlyValid(leaf, now);
        var pinned = options.ProjectsPins.Any(pin =>
            now < pin.RetireAtUtc &&
            CryptographicOperations.FixedTimeEquals(pin.Digest, digest));
        if (caLeaf ||
            !serverPurpose ||
            !currentlyValid ||
            !pinned)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(backendCa);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.UrlRetrievalTimeout = UrlRetrievalTimeout;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        if (peerChain is not null)
            foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);

        try
        {
            return chain.Build(leaf) &&
                chain.ChainStatus.All(status => status.Status == X509ChainStatusFlags.NoError) &&
                chain.ChainElements.Cast<X509ChainElement>()
                    .All(element => IsCurrentlyValid(element.Certificate, now));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool HasPurpose(X509Certificate2 certificate, string oid) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>()
                .Any(usage => usage.Value == oid));

    private static bool IsCurrentlyValid(X509Certificate2 certificate, DateTimeOffset now) =>
        now >= certificate.NotBefore.ToUniversalTime() &&
        now < certificate.NotAfter.ToUniversalTime();

    private static bool HasExactEnvironmentSan(X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .ToArray();
        if (extensions.Length != 1)
            return false;

        try
        {
            var reader = new AsnReader(extensions[0].RawData, AsnEncodingRules.DER);
            var names = reader.ReadSequence();
            if (!names.HasData)
                return false;
            var tag = names.PeekTag();
            if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue != 2 || tag.IsConstructed)
                return false;
            var dnsName = names.ReadCharacterString(
                UniversalTagNumber.IA5String,
                new Asn1Tag(TagClass.ContextSpecific, 2));
            names.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            return string.Equals(
                dnsName,
                PlatformPolicySourceClientTls.EnvironmentDnsName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private sealed class OwnedHandler(
        HttpMessageHandler innerHandler,
        ValidationCallbackLifetime validationLifetime)
        : DelegatingHandler(innerHandler), IPlatformPolicySourceHandlerLifetime
    {
        public Task ValidationCompleted => validationLifetime.Completion;

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                validationLifetime.Close();
            }
        }
    }

    private sealed class ValidationCallbackLifetime(Action releaseCertificates)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCallbacks;
        private bool _closed;
        private bool _released;

        internal Task Completion => _completion.Task;

        internal bool TryEnter()
        {
            lock (_gate)
            {
                if (_closed)
                    return false;
                _activeCallbacks++;
                return true;
            }
        }

        internal void Exit()
        {
            Action? release;
            lock (_gate)
            {
                _activeCallbacks--;
                release = TakeRelease();
            }
            Release(release);
        }

        internal void Close()
        {
            Action? release;
            lock (_gate)
            {
                _closed = true;
                release = TakeRelease();
            }
            Release(release);
        }

        private Action? TakeRelease()
        {
            if (!_closed || _activeCallbacks != 0 || _released)
                return null;
            _released = true;
            return releaseCertificates;
        }

        private void Release(Action? release)
        {
            if (release is null)
                return;
            try
            {
                release();
                _completion.TrySetResult();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
