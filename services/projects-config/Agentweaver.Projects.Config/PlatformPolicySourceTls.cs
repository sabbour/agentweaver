using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Agentweaver.Projects.Config;

internal static class PlatformPolicySourceTls
{
    internal const string SectionName = "ProjectsConfig:PolicySource";
    internal const string EndpointName = "EnvironmentPolicySource";
    internal const string EndpointUrlKey = "Kestrel:Endpoints:EnvironmentPolicySource:Url";
    internal const string RoutePath = "/internal/policy-source/platform";
    internal const string BackendCaFile = "/var/run/service-backend-ca/ca.crt";
    internal const string EnvironmentDnsName = "environment.agentweaver.svc.cluster.local";

    private const string PublicEndpointName = "ProjectsHttps";
    private const string PublicEndpointUrl = "https://+:8443";
    private const string PrivateEndpointUrl = "https://+:8445";

    internal static bool Configure(
        WebApplicationBuilder builder,
        string backendCaFile = BackendCaFile,
        TimeProvider? timeProvider = null)
    {
        var enabledValue = builder.Configuration[$"{SectionName}:Enabled"];
        if (enabledValue is not null && !bool.TryParse(enabledValue, out _))
            throw InvalidConfiguration();
        var enabled = bool.TryParse(enabledValue, out var parsedEnabled) && parsedEnabled;
        if (!enabled)
        {
            if (builder.Configuration[EndpointUrlKey] is not null)
                throw InvalidConfiguration();
            return false;
        }

        var options = ReadOptions(builder.Configuration.GetSection(SectionName), (timeProvider ?? TimeProvider.System).GetUtcNow());
        var identity = new PlatformPolicySourceTlsIdentity(options, backendCaFile, timeProvider ?? TimeProvider.System);
        builder.Services.AddSingleton(identity);

        var publicEndpointName = ConfigureEndpointUrls(builder);
        KestrelEndpointComposition.AddEndpoint(builder, (loader, server) =>
        {
            loader.Endpoint(publicEndpointName, _ => { });
            loader.Endpoint(EndpointName, endpoint =>
            {
                var tls = server.ApplicationServices.GetRequiredService<PlatformPolicySourceTlsIdentity>();
                endpoint.HttpsOptions.SslProtocols = PlatformPolicySourceTlsIdentity.AllowedProtocols;
                endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                endpoint.HttpsOptions.CheckCertificateRevocation = true;
                endpoint.HttpsOptions.ClientCertificateValidation = tls.AcceptClientCertificate;
            });
        });
        return true;
    }

    private static string ConfigureEndpointUrls(WebApplicationBuilder builder)
    {
        var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().ToArray();
        if (endpoints.Any(endpoint =>
                string.Equals(endpoint.Key, EndpointName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.Equals(builder.Configuration[EndpointUrlKey], PrivateEndpointUrl, StringComparison.Ordinal))
                throw InvalidConfiguration();
        }
        else
        {
            builder.Configuration[EndpointUrlKey] = PrivateEndpointUrl;
        }

        var privateEndpoints = endpoints
            .Where(endpoint => ReadEndpointUris(endpoint["Url"]).Any(address => address.Port == 8445))
            .ToArray();
        if (privateEndpoints.Any(endpoint =>
                !string.Equals(endpoint.Key, EndpointName, StringComparison.OrdinalIgnoreCase)) ||
            privateEndpoints.Length > 1)
            throw InvalidConfiguration();

        var publicEndpoints = endpoints
            .Where(endpoint => ReadEndpointUris(endpoint["Url"]).Any(address => address.Port == 8443))
            .ToArray();
        if (publicEndpoints.Length > 1 ||
            publicEndpoints.Any(endpoint =>
                ReadEndpointUris(endpoint["Url"]).Any(address =>
                    address.Port == 8443 && address.Scheme != Uri.UriSchemeHttps)))
            throw InvalidConfiguration();
        if (publicEndpoints.Length == 1)
            return publicEndpoints[0].Key;

        if (builder.Configuration[$"Kestrel:Endpoints:{PublicEndpointName}:Url"] is not null)
            throw InvalidConfiguration();
        builder.Configuration[$"Kestrel:Endpoints:{PublicEndpointName}:Url"] = PublicEndpointUrl;
        return PublicEndpointName;
    }

    private static IEnumerable<Uri> ReadEndpointUris(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var addresses = new List<Uri>();
        foreach (var url in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = url.Replace("://+:", "://localhost:", StringComparison.Ordinal)
                .Replace("://*:", "://localhost:", StringComparison.Ordinal);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var address) ||
                address.Port is < 1 or > 65535)
                return [];
            addresses.Add(address);
        }
        return addresses;
    }

    private static PlatformPolicySourceTlsOptions ReadOptions(
        IConfigurationSection section,
        DateTimeOffset now)
    {
        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Enabled",
            "BackendCaSha256",
            "EnvironmentPins",
        };
        if (section.GetChildren().Any(child => !allowedKeys.Contains(child.Key)))
            throw InvalidConfiguration();

        var caSha256 = section["BackendCaSha256"];
        var pinSection = section.GetSection("EnvironmentPins");
        var pinItems = pinSection.GetChildren()
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (!IsSha256(caSha256) || pinItems.Length is < 1 or > 4)
            throw InvalidConfiguration();

        var pins = ImmutableArray.CreateBuilder<PlatformPolicySourceLeafPin>(pinItems.Length);
        for (var index = 0; index < pinItems.Length; index++)
        {
            var item = pinItems[index];
            if (item.Key != index.ToString(CultureInfo.InvariantCulture) ||
                item.GetChildren().Any(child => child.Key is not ("Sha256" or "RetireAtUtc")))
                throw InvalidConfiguration();

            var sha256 = item["Sha256"];
            var retirementValue = item["RetireAtUtc"];
            if (!IsSha256(sha256) ||
                retirementValue is null ||
                !retirementValue.EndsWith('Z') ||
                !DateTimeOffset.TryParse(
                    retirementValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var retireAtUtc) ||
                retireAtUtc <= now)
                throw InvalidConfiguration();
            pins.Add(new PlatformPolicySourceLeafPin(sha256!, retireAtUtc));
        }

        if (pins.Select(pin => pin.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Count)
            throw InvalidConfiguration();
        return new PlatformPolicySourceTlsOptions(caSha256!, pins.MoveToImmutable());
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static InvalidOperationException InvalidConfiguration() =>
        new("ProjectsConfig:PolicySource requires one complete, valid Environment TLS profile.");
}

internal sealed record PlatformPolicySourceLeafPin(string Sha256, DateTimeOffset RetireAtUtc);

internal sealed record PlatformPolicySourceTlsOptions(
    string BackendCaSha256,
    ImmutableArray<PlatformPolicySourceLeafPin> EnvironmentPins);

internal sealed class PlatformPolicySourceTlsIdentity
{
    internal const SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const int MaximumPeerIntermediates = 8;
    private const int MaximumPeerIntermediateBytes = 262_144;
    private static readonly TimeSpan UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
    private readonly PlatformPolicySourceTlsOptions _options;
    private readonly byte[] _backendCaDigest;
    private readonly ImmutableArray<(byte[] Digest, DateTimeOffset RetireAtUtc)> _pins;
    private readonly string _backendCaFile;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ImmutableArray<byte[]>> _peerIntermediates =
        new(StringComparer.Ordinal);

    internal PlatformPolicySourceTlsIdentity(
        PlatformPolicySourceTlsOptions options,
        string backendCaFile,
        TimeProvider timeProvider)
    {
        _options = options;
        _backendCaFile = backendCaFile;
        _timeProvider = timeProvider;
        _backendCaDigest = Convert.FromHexString(options.BackendCaSha256);
        _pins = options.EnvironmentPins
            .Select(pin => (Digest: Convert.FromHexString(pin.Sha256), pin.RetireAtUtc))
            .ToImmutableArray();

        try
        {
            using var root = LoadPinnedAuthority();
            if (!IsCertificateAuthority(root))
                throw new InvalidOperationException("The mounted backend CA is not a CA certificate.");
        }
        catch (Exception exception) when (exception is IOException or CryptographicException)
        {
            throw new InvalidOperationException(
                "ProjectsConfig:PolicySource requires the configured mounted backend CA.",
                exception);
        }
    }

    internal bool AcceptClientCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors) =>
        ValidatePeer(certificate, chain, errors, _timeProvider.GetUtcNow(), cacheIntermediates: true);

    internal async Task<bool> IsEnvironmentPeerAsync(
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        if (certificate is null ||
            !IsPinnedEnvironmentLeaf(certificate, _timeProvider.GetUtcNow(), out var digest))
            return false;

        return await Task.Run(
                () => ValidatePeer(certificate, null, SslPolicyErrors.None, _timeProvider.GetUtcNow(),
                    cacheIntermediates: false, digest, cancellationToken),
                CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private bool ValidatePeer(
        X509Certificate? certificate,
        X509Chain? peerChain,
        SslPolicyErrors errors,
        DateTimeOffset now,
        bool cacheIntermediates,
        byte[]? knownDigest = null,
        CancellationToken cancellationToken = default)
    {
        var unexpectedErrors = errors & ~SslPolicyErrors.RemoteCertificateChainErrors;
        if (certificate is null || unexpectedErrors != SslPolicyErrors.None)
            return false;

        using var converted = certificate is X509Certificate2
            ? null
            : X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var leaf = certificate as X509Certificate2 ?? converted!;
        if (!IsPinnedEnvironmentLeaf(leaf, now, out var digest) ||
            knownDigest is not null &&
            !CryptographicOperations.FixedTimeEquals(knownDigest, digest))
            return false;

        try
        {
            using var root = LoadPinnedAuthority();
            using var validationChain = new X509Chain();
            validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validationChain.ChainPolicy.CustomTrustStore.Add(root);
            validationChain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
            validationChain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            validationChain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            validationChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            validationChain.ChainPolicy.DisableCertificateDownloads = false;
            validationChain.ChainPolicy.UrlRetrievalTimeout = UrlRetrievalTimeout;
            validationChain.ChainPolicy.VerificationTime = now.UtcDateTime;

            var intermediates = cacheIntermediates
                ? ReadPeerIntermediates(peerChain, root)
                : _peerIntermediates.GetValueOrDefault(Convert.ToHexString(digest), []);
            if (intermediates.IsDefault)
                return false;
            var peerIntermediates = new List<X509Certificate2>(intermediates.Length);
            try
            {
                foreach (var encoded in intermediates)
                {
                    var intermediate = X509CertificateLoader.LoadCertificate(encoded);
                    peerIntermediates.Add(intermediate);
                    validationChain.ChainPolicy.ExtraStore.Add(intermediate);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!validationChain.Build(leaf) ||
                    validationChain.ChainStatus.Any(status => status.Status != X509ChainStatusFlags.NoError))
                    return false;

                cancellationToken.ThrowIfCancellationRequested();
                var chainElements = validationChain.ChainElements;
                var verifiedAt = _timeProvider.GetUtcNow();
                if (!IsPinnedEnvironmentLeaf(leaf, verifiedAt, out var currentDigest) ||
                    !CryptographicOperations.FixedTimeEquals(digest, currentDigest) ||
                    chainElements.Cast<X509ChainElement>()
                        .Any(element => !IsCertificateCurrentlyValid(element.Certificate, verifiedAt)))
                    return false;

                using var currentRoot = LoadPinnedAuthority();
                if (cacheIntermediates)
                    _peerIntermediates[Convert.ToHexString(digest)] = intermediates;
                return true;
            }
            finally
            {
                foreach (var intermediate in peerIntermediates)
                    intermediate.Dispose();
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or UnauthorizedAccessException or
                OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsCertificateCurrentlyValid(X509Certificate2 certificate, DateTimeOffset now) =>
        now >= certificate.NotBefore.ToUniversalTime() &&
        now < certificate.NotAfter.ToUniversalTime();

    private X509Certificate2 LoadPinnedAuthority()
    {
        if (!File.Exists(_backendCaFile))
            throw new IOException("The mounted backend CA is unavailable.");
        var root = X509CertificateLoader.LoadCertificateFromFile(_backendCaFile);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(root.RawData), _backendCaDigest))
        {
            root.Dispose();
            throw new CryptographicException("The mounted backend CA fingerprint changed.");
        }
        return root;
    }

    private bool IsPinnedEnvironmentLeaf(
        X509Certificate2 certificate,
        DateTimeOffset now,
        out byte[] digest)
    {
        var leafDigest = SHA256.HashData(certificate.RawData);
        digest = leafDigest;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now >= certificate.NotAfter.ToUniversalTime() ||
            certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                .Any(extension => extension.CertificateAuthority) ||
            !HasClientAuthenticationPurpose(certificate) ||
            !HasExactEnvironmentSan(certificate))
            return false;

        return _pins.Any(pin =>
            now < pin.RetireAtUtc &&
            CryptographicOperations.FixedTimeEquals(pin.Digest, leafDigest));
    }

    private static bool HasClientAuthenticationPurpose(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>()
                .Any(usage => usage.Value == ClientAuthenticationOid));

    private static bool IsCertificateAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority);

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
                PlatformPolicySourceTls.EnvironmentDnsName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static ImmutableArray<byte[]> ReadPeerIntermediates(X509Chain? peerChain, X509Certificate2 root)
    {
        if (peerChain is null)
            return [];

        var rootDigest = SHA256.HashData(root.RawData);
        var result = ImmutableArray.CreateBuilder<byte[]>();
        var totalBytes = 0;
        foreach (var element in peerChain.ChainElements.Cast<X509ChainElement>().Skip(1))
        {
            var raw = element.Certificate.RawData;
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), rootDigest))
                continue;
            if (raw.Length > MaximumPeerIntermediateBytes - totalBytes)
                return default;
            totalBytes += raw.Length;
            if (result.Count >= MaximumPeerIntermediates ||
                totalBytes > MaximumPeerIntermediateBytes)
                return default;
            result.Add(raw.ToArray());
        }
        return result.ToImmutable();
    }
}
